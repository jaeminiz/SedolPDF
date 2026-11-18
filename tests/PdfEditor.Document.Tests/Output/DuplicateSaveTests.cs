using Pdf.Native;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document.Content;
using PdfEditor.Document.Output;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Document.Tests.Output;

/// <summary>0.9.0 Task 5: 개체 복제가 있는 쪽을 내용 스트림 고쳐 쓰기로 저장하고 다시 열어 확인한다.</summary>
public sealed class DuplicateSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-dupsave-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public DuplicateSaveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    private static OutputCopyBuilder Builder(DocumentSession s) => new(null, new PdfSharpAnnotationPatcher(() => []), s.Assets);

    private async Task<DocumentSession> OpenWith(Action<string> make, string? password = null)
    {
        string p = P(Guid.NewGuid().ToString("N") + ".pdf");
        make(p);
        if (password is not null)
        {
            using var doc = PdfReader.Open(p, PdfDocumentOpenMode.Modify);
            doc.SecuritySettings.UserPassword = password;
            doc.SecuritySettings.OwnerPassword = password + "-owner";
            doc.Save(p);
        }
        return await DocumentSession.OpenAsync(_t, p, password);
    }

    private static int Diff(byte[] a, byte[] b)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) > 16 || Math.Abs(a[i + 1] - b[i + 1]) > 16 || Math.Abs(a[i + 2] - b[i + 2]) > 16) n++;
        return n;
    }

    /// <summary>한 객체만 켜고 그 경계(+2pt)를 scale 1로 그린 BGRA. 회전 0 쪽 가정. 셋째 값은 (경계 높이 기준) 위쪽 y.</summary>
    private static byte[] RenderOnly(PdfPage page, int index, PdfRectF region, out int w, out int h)
    {
        int n = page.ObjectCount;
        for (int i = 0; i < n; i++) page.GetObject(i).IsActive = i == index;
        float pageH = page.SizePt.Height;
        int x = (int)Math.Floor(region.Left) - 2, y = (int)Math.Floor(pageH - region.Top) - 2;
        w = (int)Math.Ceiling(region.Width) + 4; h = (int)Math.Ceiling(region.Height) + 4;
        return page.RenderTile(1.0, x, y, w, h, false);
    }

    [Fact]
    public async Task SamePage_PathTextForm_SaveReopen_LooksLikeMovedCopies()
    {
        await using var s = await OpenWith(p => ContentPdfFactory.CreateDrawingLike(p, gradient: false));
        var id = s.Pages[0].Id;
        var picked = await _t.InvokeAsync(() =>
        {
            var idx = s.GetObjectIndex(id);
            var path = idx.Entries.First(e => e.Type == PdfPageObjectType.Path && Math.Abs(e.Bounds.Width - 200) < 3 && Math.Abs(e.Bounds.Height - 120) < 3);
            var text = idx.Entries.First(e => e.Type == PdfPageObjectType.Text && (e.TextPreview ?? "").StartsWith("TITLE"));
            var form = idx.Entries.First(e => e.Type == PdfPageObjectType.Form);
            return (Picks: new[] { path, text, form }.Select(e => (e.Ref.OriginalIndex, e.Bounds)).ToArray(), Count: s.GetLoadedPage(id).ObjectCount);
        });
        var (picks, originalCount) = (picked.Picks, picked.Count);
        var shift = PdfMatrix.Translate(0, -200);
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [.. picks.Select(p => (ContentEdit)new DuplicateObjectEdit(s.Content.NewId(), id, p.OriginalIndex, shift))]));
        string outPath = P("out.pdf");
        await Builder(s).BuildAsync(s, outPath);

        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, outPath, null);
            using var page = doc.LoadPage(0);
            int total = page.ObjectCount;
            Assert.Equal(originalCount + 3, total);
            for (int k = 0; k < 3; k++)
            {
                var b = picks[k].Bounds;
                var orig = RenderOnly(page, picks[k].OriginalIndex, b, out int w, out int h);
                var copyBounds = new PdfRectF(b.Left, b.Bottom - 200, b.Right, b.Top - 200);
                var copy = RenderOnly(page, total - 3 + k, copyBounds, out int w2, out int h2);
                Assert.Equal((w, h), (w2, h2));
                int diff = Diff(orig, copy);
                Assert.True(diff <= 0.02 * (w * h) + 8, $"kind {k}: {diff} of {w * h} pixels differ");
            }
            return 0;
        });
    }


    [Fact]
    public async Task CrossPage_FontNameClash_RendersWithSourceFont()
    {
        await using var s = await OpenWith(ContentPdfFactory.CreateTwoPageFontClash);
        var p1 = s.Pages[0].Id; var p2 = s.Pages[1].Id;
        int idx = await _t.InvokeAsync(() => s.GetObjectIndex(p1).Entries.First(e => e.Type == PdfPageObjectType.Text).Ref.OriginalIndex);
        await s.Commands.ExecuteAsync(new ContentEditCommand(p2, [new DuplicateObjectEdit(s.Content.NewId(), p1, idx, PdfMatrix.Translate(0, -100))]));
        string outPath = P("out.pdf");
        await Builder(s).BuildAsync(s, outPath);

        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, outPath, null);
            using var page = doc.LoadPage(1);
            using var tp = page.LoadText();
            var texts = Enumerable.Range(0, page.ObjectCount).Select(page.GetObject).Where(o => o.Type == PdfPageObjectType.Text)
                .Select(o => (Text: o.GetText(tp), Font: o.Font?.BaseName)).ToList();
            Assert.Equal(2, texts.Count);
            var src = Assert.Single(texts, t => (t.Text ?? "").Contains("SOURCE"));
            Assert.Contains("Arial", src.Font, StringComparison.OrdinalIgnoreCase);
            var tgt = Assert.Single(texts, t => (t.Text ?? "").Contains("TARGET"));
            Assert.DoesNotContain("Arial", tgt.Font, StringComparison.OrdinalIgnoreCase);
            return 0;
        });
    }

    [Fact]
    public async Task Duplicate_OfDuplicate_SavesOneSnippetFromOriginal()
    {
        await using var s = await OpenWith(p => ContentPdfFactory.CreateDocumentLike(p));
        var id = s.Pages[0].Id;
        var (idx, b, count) = await _t.InvokeAsync(() =>
        {
            var e = s.GetObjectIndex(id).Entries.Where(x => x.Type == PdfPageObjectType.Path && x.Bounds.Width < 400).OrderBy(x => x.Order).First();
            return (e.Ref.OriginalIndex, e.Bounds, s.GetLoadedPage(id).ObjectCount);
        });
        long did = s.Content.NewId();
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [new DuplicateObjectEdit(did, id, idx, PdfMatrix.Translate(0, -100))]));
        // 사본을 복사해 붙이면 사본이 아니라 원본 하나를 가리키는 편집이 된다(합친 행렬)
        var copy = await _t.InvokeAsync(() => ObjectCopyBuilder.Build(s, id, [ObjectRef.Inserted(did)]));
        var (edits, _) = ObjectPasteBuilder.Build(s.Content, copy.Items, PdfMatrix.Translate(0, -100));
        var d2 = Assert.IsType<DuplicateObjectEdit>(Assert.Single(edits));
        Assert.Equal(idx, d2.SourceIndex);
        Assert.Equal(PdfMatrix.Translate(0, -200), d2.Then);
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, edits));
        string outPath = P("out.pdf");
        await Builder(s).BuildAsync(s, outPath);
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, outPath, null);
            using var page = doc.LoadPage(0);
            Assert.Equal(count + 2, page.ObjectCount);
            var last = page.GetObject(page.ObjectCount - 1).Bounds;
            Assert.InRange(last.Bottom, b.Bottom - 200 - 1, b.Bottom - 200 + 1);
            return 0;
        });
    }

    [Fact]
    public async Task Duplicate_OnUnpatchableDocument_RefusesSave()
    {
        await using var s = await OpenWith(p => ContentPdfFactory.CreateDocumentLike(p), password: "pw");
        var id = s.Pages[0].Id;
        int idx = await _t.InvokeAsync(() => s.GetObjectIndex(id).Entries.First(e => e.Type == PdfPageObjectType.Path).Ref.OriginalIndex);
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [new DuplicateObjectEdit(s.Content.NewId(), id, idx, PdfMatrix.Translate(5, 5))]));
        byte[] before = File.ReadAllBytes(s.FilePath!);
        await Assert.ThrowsAsync<ContentRegenerationException>(() => Builder(s).BuildAsync(s, P("out.pdf")));
        Assert.Equal(before, File.ReadAllBytes(s.FilePath!));
    }
}

/// <summary>화면 모양≡저장 모양 검사에 걸려도 사용자가 확인하면 저장은 되어야 한다(2026-11-17 사용자 결정).</summary>
public sealed class LookChangeAcceptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-lookacc-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    public LookChangeAcceptTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { _t.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task EncryptedDocWithCopy_Refuses_ButAcceptStillRefuses_NonLookFailures()
    {
        // 모양 검사가 아닌 실패(암호 문서라 복사 확정을 못 함)는 "그래도 저장"으로도 넘어가지 않는다.
        string p = Path.Combine(_dir, "a.pdf");
        ContentPdfFactory.CreateDocumentLike(p);
        using (var doc = PdfReader.Open(p, PdfDocumentOpenMode.Modify))
        {
            doc.SecuritySettings.UserPassword = "pw"; doc.SecuritySettings.OwnerPassword = "pw-owner"; doc.Save(p);
        }
        await using var s = await DocumentSession.OpenAsync(_t, p, "pw");
        var id = s.Pages[0].Id;
        int idx = await _t.InvokeAsync(() => s.GetObjectIndex(id).Entries.First(e => e.Type == PdfPageObjectType.Path).Ref.OriginalIndex);
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [new DuplicateObjectEdit(s.Content.NewId(), id, idx, PdfMatrix.Translate(5, 5))]));
        var b = new OutputCopyBuilder(null, new PdfSharpAnnotationPatcher(() => []), s.Assets) { AcceptLookChanges = true };
        await Assert.ThrowsAsync<ContentRegenerationException>(() => b.BuildAsync(s, Path.Combine(_dir, "out.pdf")));
    }

    [Fact]
    public async Task GradientPage_MoveWithoutRewritePath_ThrowsNormally_AndSavesWithWarningWhenAccepted()
    {
        string p = Path.Combine(_dir, "g.pdf");
        ContentPdfFactory.CreateDrawingLike(p, gradient: true);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        var id = s.Pages[0].Id;
        var all = await _t.InvokeAsync(() => s.GetObjectIndex(id).Entries.Select(e => e.Ref).ToList());
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [.. all.Select(r => (ContentEdit)new TransformEdit(s.Content.NewId(), r, PdfMatrix.Translate(3, -3)))]));
        // 패치기 없음 = 고쳐 쓰기 불가 → PDFium 경로만. 셰이딩이 없는 도면이면 그냥 통과하므로 결과 둘 중 하나를 확인한다.
        string o1 = Path.Combine(_dir, "o1.pdf");
        bool threw = false;
        try { await new OutputCopyBuilder(null, null, s.Assets).BuildAsync(s, o1); } catch (ContentRegenerationException) { threw = true; }
        string o2 = Path.Combine(_dir, "o2.pdf");
        var copy = await new OutputCopyBuilder(null, null, s.Assets) { AcceptLookChanges = true }.BuildAsync(s, o2);
        Assert.True(File.Exists(o2));
        Assert.True(threw, "셰이딩 쪽의 PDFium 다시 쓰기는 모양 검사에 걸려야 한다");
        Assert.NotEmpty(copy.LookWarnings!);
    }
}
