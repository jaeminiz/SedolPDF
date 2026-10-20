using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf.IO;

namespace PdfEditor.TextEdit.Tests;

/// <summary>M4 Task 5: 텍스트 한 줄 바꾸기(원래 글꼴 → 앱 글꼴 대체), 새 텍스트, 쪽 내용 덧붙이기.</summary>
public sealed class TextReplaceTests : IDisposable
{
    private static readonly Lazy<AppFont> Font = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        return AppFont.Load(Path.Combine(dir!.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"));
    });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-textedit-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public TextReplaceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    private async Task<DocumentSession> Open(bool stripToUnicode = false, string? password = null)
    {
        string p = P(Guid.NewGuid().ToString("N") + ".pdf");
        ContentPdfFactory.CreateDocumentLike(p, stripToUnicode);
        if (password is not null)
        {
            using var d = PdfReader.Open(p, PdfDocumentOpenMode.Modify);
            d.SecuritySettings.UserPassword = password;
            d.SecuritySettings.OwnerPassword = password + "-o";
            d.Save(p);
        }
        return await DocumentSession.OpenAsync(_t, p, password);
    }

    private SaveService Svc() => new([], null, new PdfSharpAnnotationPatcher(Font.Value));

    private static ObjectRef TextLine(DocumentSession s, string startsWith) =>
        s.GetObjectIndex(s.Pages[0].Id).Entries.First(e => e.Type == PdfPageObjectType.Text && (e.TextPreview ?? "").StartsWith(startsWith)).Ref;

    /// <summary>ToUnicode가 없으면 글로 찾을 수 없으므로 두 번째 텍스트 객체(맑은 고딕 줄).</summary>
    private static ObjectRef SecondText(DocumentSession s) =>
        s.GetObjectIndex(s.Pages[0].Id).Entries.Where(e => e.Type == PdfPageObjectType.Text).OrderBy(e => e.Order).ElementAt(1).Ref;

    private Task<ReplacePlan> Plan(DocumentSession s, Func<DocumentSession, ObjectRef> target, string text, AppFont? font) =>
        _t.InvokeAsync(() => TextEditPlanner.PlanReplace(s, s.Pages[0].Id, target(s), text, font));

    private static async Task Run(DocumentSession s, ReplacePlan plan) =>
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, plan.Edits));

    private (string Text, List<string> Fonts) Saved(string path, string? password = null)
    {
        string text = _t.InvokeAsync(() =>
        {
            using var d = PdfDocument.Open(_t, path, password);
            using var p = d.LoadPage(0);
            using var tp = p.LoadText();
            return tp.GetText(0, tp.CharCount);
        }).GetAwaiter().GetResult();
        using var doc = password is null ? PdfReader.Open(path, PdfDocumentOpenMode.Import) : PdfReader.Open(path, password, PdfDocumentOpenMode.Import);
        var fonts = doc.Pages[0].Resources.Elements.GetDictionary("/Font")!.Elements
            .Select(kv => (kv.Value is PdfSharp.Pdf.Advanced.PdfReference r ? r.Value : kv.Value) as PdfSharp.Pdf.PdfDictionary)
            .Select(d => d?.Elements.GetName("/BaseFont") ?? "").ToList();
        return (text, fonts);
    }

    [Fact]
    public async Task ReplaceText_OriginalFontRoundTrip_Kept()
    {
        await using var s = await Open();
        var plan = await Plan(s, x => TextLine(x, "Arial"), "Arial 321 CBA", Font.Value);
        Assert.Equal(ReplaceRefusal.None, plan.Refusal);
        Assert.False(plan.FallbackUsed);
        Assert.Contains(plan.Edits, e => e is InsertTextLikeEdit);
        await Run(s, plan);
        await Svc().SaveAsync(s, P("o.pdf"));
        var (text, fonts) = Saved(P("o.pdf"));
        Assert.Contains("Arial 321 CBA", text);
        Assert.DoesNotContain(ContentPdfFactory.LineArial, text);
        Assert.DoesNotContain(fonts, f => f.Contains("NotoSansKR"));
    }

    [Fact]
    public async Task ReplaceText_MissingGlyph_FallsBackToAppFont_TextExtractable()
    {
        await using var s = await Open();
        var plan = await Plan(s, x => TextLine(x, "Arial"), "Zebra Quiz 789 가나", Font.Value);
        Assert.True(plan.FallbackUsed);
        await Run(s, plan);
        await Svc().SaveAsync(s, P("f.pdf"));
        var (text, fonts) = Saved(P("f.pdf"));
        Assert.Contains("Zebra Quiz 789 가나", text);
        Assert.DoesNotContain(ContentPdfFactory.LineArial, text);
        Assert.Single(fonts, f => f.Contains("NotoSansKR"));
    }

    [Fact]
    public async Task ReplaceText_Unverifiable_NoToUnicode_FallsBackToAppFont()
    {
        await using var s = await Open(stripToUnicode: true);
        var plan = await Plan(s, SecondText, "다나가 검토", Font.Value);
        Assert.Equal(ReplaceRefusal.None, plan.Refusal);
        Assert.True(plan.FallbackUsed);
    }

    [Fact]
    public async Task ReplaceText_Encrypted_NoAppFont_RefusedWithReason()
    {
        await using var s = await Open(password: "pw");
        var plan = await Plan(s, x => TextLine(x, "Arial"), "Zebra", Font.Value);
        Assert.Equal(ReplaceRefusal.Encrypted, plan.Refusal);
        Assert.Empty(plan.Edits);
        // 원래 글꼴로 되는 글은 암호 문서에서도 된다
        var ok = await Plan(s, x => TextLine(x, "Arial"), "Arial 123", Font.Value);
        Assert.Equal(ReplaceRefusal.None, ok.Refusal);
        Assert.False(ok.FallbackUsed);
    }

    [Fact]
    public async Task ReplaceText_Empty_DeactivatesLine()
    {
        await using var s = await Open();
        var plan = await Plan(s, x => TextLine(x, "Arial"), "  ", Font.Value);
        Assert.IsType<DeactivateEdit>(Assert.Single(plan.Edits));
    }

    [Fact]
    public async Task AppFontLine_ScreenOutlinesMatchFileGlyphs()
    {
        await using var s = await Open();
        await Run(s, await Plan(s, x => TextLine(x, "Arial"), "검토 완료 OK 2026", Font.Value));
        var id = s.Pages[0].Id;
        var (screen, bounds) = await _t.InvokeAsync(() =>
        {
            var b = s.GetObjectIndex(id).Entries.Single(e => e.Ref.IsInserted).Bounds;
            return (s.GetLoadedPage(id).RenderTile(2, 0, 0, 1190, 1684, false), b);
        });
        await Svc().SaveAsync(s, P("m.pdf"));
        var file = await _t.InvokeAsync(() =>
        {
            using var d = PdfDocument.Open(_t, P("m.pdf"));
            using var p = d.LoadPage(0);
            return p.RenderTile(2, 0, 0, 1190, 1684, false);
        });
        // 글자 줄 상자 안에서 비교 (안티앨리어싱 차이 허용)
        int x0 = (int)(bounds.Left * 2), x1 = (int)(bounds.Right * 2), y0 = (int)((842 - bounds.Top) * 2), y1 = (int)((842 - bounds.Bottom) * 2);
        int total = 0, diff = 0, inkScreen = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int o = (y * 1190 + x) * 4;
                total++;
                if (screen[o] < 128) inkScreen++;
                if (Math.Abs(screen[o] - file[o]) > 60) diff++;
            }
        Assert.True(inkScreen > total / 50, "screen must show the new line");
        Assert.True(diff < total * 0.03, $"screen vs file differ on {diff}/{total} pixels");
    }

    [Fact]
    public async Task PageContentAppend_SaveTwice_OneFontOneStream()
    {
        await using var s = await Open();
        await Run(s, await Plan(s, x => TextLine(x, "Arial"), "Zebra", Font.Value));
        await Svc().SaveAsync(s, P("1.pdf"));
        await Svc().SaveAsync(s, P("2.pdf"));
        Assert.Single(Saved(P("1.pdf")).Fonts, f => f.Contains("NotoSansKR"));
        Assert.Single(Saved(P("2.pdf")).Fonts, f => f.Contains("NotoSansKR"));
    }

    [Fact]
    public async Task AddText_KoreanEnglishDigits_SavedAsRealText()
    {
        await using var s = await Open();
        var plan = await _t.InvokeAsync(() => TextEditPlanner.PlanAdd(s, new PdfPointF(300, 700), "가나다 ABC 123", 12, new PdfColor(0, 0, 0), Font.Value));
        Assert.Equal(ReplaceRefusal.None, plan.Refusal);
        await Run(s, plan);
        await Svc().SaveAsync(s, P("a.pdf"));
        Assert.Contains("가나다 ABC 123", Saved(P("a.pdf")).Text);
    }

    [Fact]
    public async Task AppTextOnEncryptedSave_Throws_NotSilentlyDropped()
    {
        await using var s = await Open(password: "pw");
        // 패치 불가 문서에 앱 글꼴 줄을 억지로 넣으면(화면은 막지만) 저장이 조용히 버리지 않고 실패한다
        var (spec, _) = AppFontLine.Build(Font.Value, "X", 12, PdfMatrix.Translate(100, 100), new PdfColor(0, 0, 0));
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, [new InsertAppTextEdit(s.Content.NewId(), spec!)]));
        await Assert.ThrowsAsync<SaveException>(() => Svc().SaveAsync(s, P("enc.pdf")));
    }
}
