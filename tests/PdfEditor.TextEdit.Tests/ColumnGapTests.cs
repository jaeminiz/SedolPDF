using System.Text;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document;
using PdfEditor.Document.Content;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf.IO;

namespace PdfEditor.TextEdit.Tests;

/// <summary>0.13.0 2차: 표제란 한 줄(TJ 칸 간격)의 날짜만 고쳐도 칸 간격이 그대로다.</summary>
public sealed class ColumnGapTests : IDisposable
{
    private static readonly Lazy<AppFont> Font = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        return AppFont.Load(Path.Combine(dir!.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"));
    });

    private readonly PdfiumThread _t = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-colgap-" + Guid.NewGuid().ToString("N"));

    public ColumnGapTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Out(string n) => Path.Combine(_dir, n);

    private async Task<(DocumentSession S, ObjectRef Ref, string Src)> Open(bool trailingGap = false)
    {
        string src = Out("src-" + Guid.NewGuid().ToString("N") + ".pdf");
        ContentPdfFactory.CreateThreeColumnTjLine(src, trailingGap);
        var s = await DocumentSession.OpenAsync(_t, src, null);
        var r = await _t.InvokeAsync(() => s.GetObjectIndex(s.Pages[0].Id).Entries.First(e => e.Type == PdfPageObjectType.Text && (e.TextPreview ?? "").StartsWith("N/S")).Ref);
        return (s, r, src);
    }

    private async Task<string> EditAndSave(DocumentSession s, ObjectRef r, string newText, string name)
    {
        var plan = await _t.InvokeAsync(() => TextEditPlanner.PlanReplace(s, s.Pages[0].Id, r, newText, Font.Value));
        Assert.Equal(ReplaceRefusal.None, plan.Refusal);
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, plan.Edits));
        string saved = Out(name);
        await new SaveService([], null, new PdfSharpAnnotationPatcher(Font.Value), s.Assets).SaveAsync(s, saved);
        return saved;
    }

    private Task<(string Text, List<float> Lefts)> Read(string pdf) => _t.InvokeAsync(() =>
    {
        using var d = Pdf.Native.PdfDocument.Open(_t, pdf, null);
        using var p = d.LoadPage(0);
        using var tp = p.LoadText();
        string all = tp.GetText(0, tp.CharCount);
        var lefts = new List<float>();
        for (int i = 0; i < all.Length; i++) lefts.Add(char.IsWhiteSpace(all[i]) ? float.NaN : tp.GetRects(i, 1)[0].X);
        return (all, lefts);
    });

    private static float LeftOf(string all, List<float> lefts, string word) => lefts[all.IndexOf(word, StringComparison.Ordinal)];

    private static string TjOf(string pdf)
    {
        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        var loaded = PdfEditor.ContentStream.PageContent.Load(doc.Pages[0]);
        return Encoding.Latin1.GetString(loaded.Data!);
    }

    [Fact]
    public async Task DateOnlyEdit_KeepsColumnPositionsAndTjGaps()
    {
        var (s, r, src) = await Open();
        var (baseText, baseLefts) = await Read(src);
        Assert.Contains("190510", baseText);
        string saved = await EditAndSave(s, r, "N/S 191120 E.J JUNG", "date.pdf");
        var (text, lefts) = await Read(saved);

        Assert.Contains("191120", text);
        Assert.DoesNotContain("190510", text);
        Assert.Equal(LeftOf(baseText, baseLefts, "N/S"), LeftOf(text, lefts, "N/S"), 0.5);
        Assert.Equal(LeftOf(baseText, baseLefts, "190510"), LeftOf(text, lefts, "191120"), 0.5);
        Assert.Equal(LeftOf(baseText, baseLefts, "E.J"), LeftOf(text, lefts, "E.J"), 0.5);
        string tj = TjOf(saved);
        Assert.Contains("-3394", tj);
        Assert.Contains("-1924", tj);
    }

    private async Task<List<string>> TextObjects(string pdf)
    {
        var s = await DocumentSession.OpenAsync(_t, pdf, null);
        return await _t.InvokeAsync(() => s.GetObjectIndex(s.Pages[0].Id).Entries.Where(e => e.Type == PdfPageObjectType.Text).Select(e => e.TextPreview ?? "").ToList());
    }

    /// <summary>0.14.0 손 시험: 고친 줄을 다시 열면 칸마다 한 덩어리여야 한다(낱글자로 쪼개지면 안 됨).</summary>
    [Theory]
    [InlineData("N/S 191120 E.J JUNG", false)]
    [InlineData("N/S 301111 E.J JUNG", false)]
    [InlineData("N/S 191120 E.J JUNG", true)]
    public async Task EditedLine_ReopensWithSameTextObjectCount(string newText, bool trailingGap)
    {
        var (s, r, src) = await Open(trailingGap);
        var before = await TextObjects(src);
        string saved = await EditAndSave(s, r, trailingGap ? newText.Replace(" JUNG", " JUNG") : newText, "reopen-" + Guid.NewGuid().ToString("N") + ".pdf");
        var after = await TextObjects(saved);
        Assert.True(before.Count == after.Count, "before: " + string.Join("|", before) + "  after: " + string.Join("|", after) + "\n" + TjOf(saved));
    }

    [Fact]
    public async Task DifferentLengthEdit_KeepsLaterColumnsInPlace()
    {
        var (s, r, src) = await Open();
        var (baseText, baseLefts) = await Read(src);
        string saved = await EditAndSave(s, r, "N/S 1905101 E.J JUNG", "len.pdf");   // 날짜 끝에 한 글자 더(같은 글자 수 아님, 칸 끝)
        var (text, lefts) = await Read(saved);
        Assert.Contains("19051", text);
        Assert.DoesNotContain("190510", text);
        Assert.Equal(LeftOf(baseText, baseLefts, "E.J"), LeftOf(text, lefts, "E.J"), 0.5);
        Assert.Equal(LeftOf(baseText, baseLefts, "N/S"), LeftOf(text, lefts, "N/S"), 0.5);
    }

    [Fact]
    public async Task EditAcrossColumnGap_FallsBackToWholeLine()
    {
        var (s, r, _) = await Open();
        string saved = await EditAndSave(s, r, "N/S 190510 0 JUNG", "cross.pdf");   // "E.J" 칸 경계를 넘어 바뀜(가짜 공백 포함 구간)
        var (text, _) = await Read(saved);
        Assert.Contains("JUNG", text);
        Assert.DoesNotContain("E.J", text);
    }

    /// <summary>실제 표제란: PDFium 글 끝에 공백이 붙어 오고 편집 칸은 끝 공백을 자른다 → 끝 공백이 달라도 부분 바꾸기(화면·저장 모두 칸 위치 유지).</summary>
    [Fact]
    public async Task TrailingGapAndTrimmedEditorText_KeepsColumnsOnScreenAndAfterSave()
    {
        var (s, r, src) = await Open(trailingGap: true);
        var (baseText, baseLefts) = await Read(src);
        string raw = await _t.InvokeAsync(() => s.GetObjectText(s.Pages[0].Id, r) ?? "");
        Assert.True(raw.Length > raw.TrimEnd().Length, "fixture should end with a (virtual) space like the real title block");
        string edited = raw.TrimEnd().Replace("190510", "191120");   // 편집 칸이 보여 주는 글(끝 공백 없음)

        var plan = await _t.InvokeAsync(() => TextEditPlanner.PlanReplace(s, s.Pages[0].Id, r, edited, Font.Value));
        Assert.Equal(ReplaceRefusal.None, plan.Refusal);
        Assert.Contains(plan.Edits, e => e is ReplaceTextSpanEdit);   // 줄 전체 다시 쓰기가 아니다
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, plan.Edits));

        // 화면(편집 중): 칸 위치가 원래와 같다
        var screen = await _t.InvokeAsync(() => s.GetObjectIndex(s.Pages[0].Id).Entries.Where(e => e.Type == PdfPageObjectType.Text && e.TextPreview is { Length: > 0 }).Select(e => (T: e.TextPreview!, L: e.Bounds.Left)).ToList());
        float Scr(string w) => screen.First(x => x.T.Contains(w, StringComparison.Ordinal)).L;
        Assert.Equal(LeftOf(baseText, baseLefts, "N/S"), Scr("N/S"), 0.5);
        Assert.Equal(LeftOf(baseText, baseLefts, "190510"), Scr("191120"), 0.5);
        Assert.Equal(LeftOf(baseText, baseLefts, "E.J"), Scr("E.J"), 0.5);

        // 저장 후
        string saved = Out("trail.pdf");
        await new SaveService([], null, new PdfSharpAnnotationPatcher(Font.Value), s.Assets).SaveAsync(s, saved);
        var (text, lefts) = await Read(saved);
        Assert.Contains("191120", text);
        Assert.DoesNotContain("190510", text);
        Assert.Equal(LeftOf(baseText, baseLefts, "190510"), LeftOf(text, lefts, "191120"), 0.5);
        Assert.Equal(LeftOf(baseText, baseLefts, "E.J"), LeftOf(text, lefts, "E.J"), 0.5);
        Assert.Contains("-3394", TjOf(saved));
    }
}
