using System.Text;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.Document.Saving;
using PdfEditor.TextEdit.Tests.Paragraphs;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfEditor.TextEdit.Tests;

/// <summary>0.13.0: 셰이딩이 있어 PDFium 다시 쓰기가 막힌 쪽의 원래 글꼴 한 줄 편집(표제란 날짜 고치기)이 내용 스트림 직접 쓰기로 저장된다.</summary>
public sealed class TitleBlockSaveTests : IDisposable
{
    private static readonly Lazy<AppFont> Font = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        return AppFont.Load(Path.Combine(dir!.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"));
    });

    private readonly PdfiumThread _t = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-tblock-" + Guid.NewGuid().ToString("N"));

    public TitleBlockSaveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Out(string n) => Path.Combine(_dir, n);

    private SaveService Svc(DocumentSession s) => new([], null, new PdfSharpAnnotationPatcher(Font.Value), s.Assets);

    private string Shaded(string name)
    {
        string dst = Out(name);
        PdfEditor.TestFixtures.ContentPdfFactory.CreateTitleBlockWithShading(dst);
        return dst;
    }

    /// <summary>글 객체 하나와 그 글(낱말 하나의 글자 순서만 뒤집은 새 글).</summary>
    private static (ObjectRef Ref, string Old, string New) Pick(DocumentSession s)
    {
        foreach (var e in s.GetObjectIndex(s.Pages[0].Id).Entries.Where(e => e.Type == PdfPageObjectType.Text && !e.Ref.IsInserted))
        {
            string t = e.TextPreview ?? "";
            foreach (var w in t.Split(' '))
            {
                if (w.Length < 4 || w.Distinct().Count() <= 2 || !w.All(char.IsLetter)) continue;
                string rev = new(w.Reverse().ToArray());
                if (rev == w) continue;
                int at = t.IndexOf(w, StringComparison.Ordinal);
                return (e.Ref, t, t[..at] + rev + t[(at + w.Length)..]);
            }
        }
        throw new InvalidOperationException("no editable word");
    }

    private Task<string> SavedText(string pdf) => _t.InvokeAsync(() =>
    {
        using var d = Pdf.Native.PdfDocument.Open(_t, pdf, null);
        using var p = d.LoadPage(0);
        using var tp = p.LoadText();
        return tp.GetText(0, tp.CharCount);
    });

    private static string Squash(string t) => string.Concat(t.Where(c => !char.IsWhiteSpace(c)));

    private static int FontCount(string pdf)
    {
        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        return doc.Internals.GetAllObjects().Count(o => o is PdfDictionary d && d.Elements.GetName("/Type") == "/Font");
    }

    private async Task<(DocumentSession S, ObjectRef Ref, string Old, string New, ReplacePlan Plan)> Edited(string name)
    {
        var s = await DocumentSession.OpenAsync(_t, Shaded(name), null);
        var (r, oldT, newT) = await _t.InvokeAsync(() => Pick(s));
        var plan = await _t.InvokeAsync(() => TextEditPlanner.PlanReplace(s, s.Pages[0].Id, r, newT, Font.Value));
        Assert.Equal(ReplaceRefusal.None, plan.Refusal);
        Assert.False(plan.FallbackUsed);
        return (s, r, oldT, newT, plan);
    }

    [Fact]
    public async Task Plan_OriginalFont_CarriesEncodedText()
    {
        var (_, _, _, newT, plan) = await Edited("plan.pdf");
        var ins = Assert.Single(plan.Edits.OfType<InsertTextLikeEdit>());
        Assert.NotNull(ins.Encoded);
        Assert.True(ins.MaskRect.Right > ins.MaskRect.Left);
        Assert.True(ins.Encoded!.Length >= newT.Length);
    }

    [Fact]
    public async Task ShadingPage_OneLineOriginalFontEdit_SavesWithoutConfirm()
    {
        var (s, _, oldT, newT, plan) = await Edited("shade.pdf");
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, plan.Edits));
        string baseline = Out("base.pdf");
        await Svc(await DocumentSession.OpenAsync(_t, Shaded("shade-src.pdf"), null)).SaveAsync(await DocumentSession.OpenAsync(_t, Out("shade-src.pdf"), null), baseline);
        string saved = Out("saved.pdf");
        await Svc(s).SaveAsync(s, saved);   // 예외 없이 저장(확인 창 없음)

        string text = Squash(await SavedText(saved));
        Assert.Contains(Squash(newT), text);
        Assert.DoesNotContain(Squash(oldT), text);
        Assert.Equal(FontCount(baseline), FontCount(saved));
    }

    [Fact]
    public async Task ShadingPage_LookUnchangedOutsideMask()
    {
        var (s, _, _, _, plan) = await Edited("look.pdf");
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, plan.Edits));
        string saved = Out("look-out.pdf");
        await Svc(s).SaveAsync(s, saved);
        var mask = plan.Edits.OfType<InsertTextLikeEdit>().Single().MaskRect;
        byte[] Render(string pdf) => _t.InvokeAsync(() =>
        {
            using var d = Pdf.Native.PdfDocument.Open(_t, pdf, null);
            using var p = d.LoadPage(0);
            return p.RenderTile(1.0, 0, 0, 595, 842, false);
        }).GetAwaiter().GetResult();
        byte[] a = Render(Out("look.pdf")), b = Render(saved);
        int diff = 0;
        for (int y = 0; y < 842; y++)
            for (int x = 0; x < 595; x++)
            {
                if (x >= mask.Left && x <= mask.Right && 842 - y >= mask.Bottom && 842 - y <= mask.Top) continue;
                int i = (y * 595 + x) * 4;
                if (Math.Abs(a[i] - b[i]) > 8 || Math.Abs(a[i + 1] - b[i + 1]) > 8 || Math.Abs(a[i + 2] - b[i + 2]) > 8) diff++;
            }
        Assert.Equal(0, diff);
    }

    [Fact]
    public async Task WithoutEncoding_SaveStillBlocked_ShowsTheFixMatters()
    {
        var (s, _, _, _, plan) = await Edited("noenc.pdf");
        var stripped = plan.Edits.Select(e => e is InsertTextLikeEdit i ? i with { Encoded = null } : e).ToList();
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, stripped));
        await Assert.ThrowsAsync<SaveException>(() => Svc(s).SaveAsync(s, Out("noenc-out.pdf")));
    }

    [Fact]
    public async Task EditThenReEdit_OnlyLastTextRemains()
    {
        var (s, r, _, firstNew, plan) = await Edited("reedit.pdf");
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, plan.Edits));
        var ins = plan.Edits.OfType<InsertTextLikeEdit>().Single();
        string second = firstNew.Length > 4 ? new string(firstNew.Reverse().ToArray()) : firstNew;
        var plan2 = await _t.InvokeAsync(() => TextEditPlanner.PlanReplace(s, s.Pages[0].Id, ObjectRef.Inserted(ins.Id), second, Font.Value));
        Assert.Equal(ReplaceRefusal.None, plan2.Refusal);
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, plan2.Edits));
        string saved = Out("reedit-out.pdf");
        await Svc(s).SaveAsync(s, saved);
        string text = Squash(await SavedText(saved));
        Assert.Contains(Squash(second), text);
        Assert.DoesNotContain(Squash(firstNew), text);
    }

    [Fact]
    public async Task EditThenMove_SavesAtMovedPosition()
    {
        var (s, _, _, newT, plan) = await Edited("move.pdf");
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, plan.Edits));
        var ins = plan.Edits.OfType<InsertTextLikeEdit>().Single();
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, [new TransformEdit(s.Content.NewId(), ObjectRef.Inserted(ins.Id), PdfMatrix.Translate(0, -30))]));
        string saved = Out("move-out.pdf");
        await Svc(s).SaveAsync(s, saved);
        double y0 = ins.Baseline.Y;
        double y = await _t.InvokeAsync(() =>
        {
            using var d = Pdf.Native.PdfDocument.Open(_t, saved, null);
            using var p = d.LoadPage(0);
            using var tp = p.LoadText();
            string all = tp.GetText(0, tp.CharCount);
            int at = Squash(all).IndexOf(Squash(newT), StringComparison.Ordinal);
            Assert.True(at >= 0);
            // 새 글의 첫 글자 위치 — 공백을 뺀 번호를 글자 번호로 되돌린다
            int idx = 0, seen = 0;
            for (; idx < all.Length; idx++) { if (!char.IsWhiteSpace(all[idx])) { if (seen == at) break; seen++; } }
            var rc = tp.GetRects(idx, 1)[0];
            return (double)rc.Y;
        });
        Assert.InRange(y, y0 - 45, y0 - 20);
    }
}
