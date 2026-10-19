using Pdf.Native;
using PdfEditor.Document.Content;
using PdfEditor.Document.Output;
using PdfEditor.Document.Recovery;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Document.Tests.Output;

/// <summary>M4 Task 4: 출력 단계 한 곳(OutputCopyBuilder)과 객체 편집 확정(내용 단계).</summary>
public sealed class OutputCopyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-output-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public OutputCopyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    private async Task<DocumentSession> Open(string? password = null)
    {
        string p = P(Guid.NewGuid().ToString("N") + ".pdf");
        ContentPdfFactory.CreateDocumentLike(p);
        if (password is not null)
        {
            using var doc = PdfReader.Open(p, PdfDocumentOpenMode.Modify);
            doc.SecuritySettings.UserPassword = password;
            doc.SecuritySettings.OwnerPassword = password + "-owner";
            doc.Save(p);
        }
        return await DocumentSession.OpenAsync(_t, p, password);
    }

    /// <summary>이미지 지우기 + 사각형 50pt 오른쪽으로.</summary>
    private Task<ContentEditCommand> Edits(DocumentSession s) => _t.InvokeAsync(() =>
    {
        var id = s.Pages[0].Id;
        var idx = s.GetObjectIndex(id);
        int img = idx.Entries.First(e => e.Type == PdfPageObjectType.Image).Ref.OriginalIndex;
        int rect = idx.Entries.Where(e => e.Type == PdfPageObjectType.Path).OrderBy(e => e.Bounds.Height).Last().Ref.OriginalIndex;
        return new ContentEditCommand(id,
        [
            new DeactivateEdit(s.Content.NewId(), ObjectRef.Original(img)),
            new TransformEdit(s.Content.NewId(), ObjectRef.Original(rect), PdfMatrix.Translate(50, 0)),
        ]);
    });

    private (int Images, float RectLeft, byte[] Render) Inspect(string path, string? password = null) => _t.InvokeAsync(() =>
    {
        using var doc = PdfDocument.Open(_t, path, password);
        using var page = doc.LoadPage(0);
        var objs = Enumerable.Range(0, page.ObjectCount).Select(page.GetObject).ToList();
        float rectLeft = objs.Where(o => o.Type == PdfPageObjectType.Path).OrderBy(o => o.Bounds.Height).Last().Bounds.Left;
        var px = page.RenderTile(0.5, 0, 0, 298, 421, false);
        return (objs.Count(o => o.Type == PdfPageObjectType.Image), rectLeft, px);
    }).GetAwaiter().GetResult();

    private static byte[] ContentBytes(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var c = doc.Pages[0].Contents;
        return c.Elements.Cast<PdfSharp.Pdf.PdfItem>().SelectMany(it =>
            (it as PdfSharp.Pdf.Advanced.PdfReference)?.Value is PdfSharp.Pdf.PdfDictionary d && d.Stream is { } st ? st.UnfilteredValue : []).ToArray();
    }

    [Fact]
    public async Task Save_AppliesEdits_SaveThreeTimes_SameContent()
    {
        await using var s = await Open();
        await s.Commands.ExecuteAsync(await Edits(s));
        var svc = new SaveService([]);
        var outs = new[] { P("a.pdf"), P("b.pdf"), P("c.pdf") };
        foreach (var o in outs) await svc.SaveAsync(s, o);
        var (images, rectLeft, _) = Inspect(outs[0]);
        Assert.Equal(0, images);
        Assert.Equal(260 + 50 - 1, rectLeft, 1.5f);   // 선 두께 2pt의 절반만큼 왼쪽
        var c0 = ContentBytes(outs[0]);
        Assert.Equal(c0, ContentBytes(outs[1]));
        Assert.Equal(c0, ContentBytes(outs[2]));
    }

    [Fact]
    public async Task EditSaveUndoSave_OriginalContentBack()
    {
        await using var s = await Open();
        var original = Inspect(s.FilePath!);
        await s.Commands.ExecuteAsync(await Edits(s));
        await new SaveService([]).SaveAsync(s, P("edited.pdf"));
        await s.Commands.UndoAsync();
        await new SaveService([]).SaveAsync(s, P("undone.pdf"));
        var back = Inspect(P("undone.pdf"));
        Assert.Equal(original.Images, back.Images);
        Assert.Equal(original.RectLeft, back.RectLeft, 0.5f);
        Assert.Equal(original.Render, back.Render);
    }

    [Fact]
    public async Task LiveDocument_NeverGenerated_AfterSave()
    {
        await using var s = await Open();
        await s.Commands.ExecuteAsync(await Edits(s));
        await new SaveService([]).SaveAsync(s, P("x.pdf"));
        // 저장한 뒤에도 편집 중인 문서의 쪽을 새로 열면 원본 그대로(이미지 있음) — 편집은 replay로만 보인다
        int images = await _t.InvokeAsync(() =>
        {
            using var page = s.Document.LoadPage(0);
            return Enumerable.Range(0, page.ObjectCount).Count(i => page.GetObject(i).Type == PdfPageObjectType.Image);
        });
        Assert.Equal(1, images);
    }

    [Fact]
    public async Task EditedPage_UntouchedRegion_PixelsUnchanged()
    {
        await using var s = await Open();
        var before = Inspect(s.FilePath!).Render;
        await s.Commands.ExecuteAsync(await Edits(s));
        await new SaveService([]).SaveAsync(s, P("e.pdf"));
        var after = Inspect(P("e.pdf")).Render;
        // 이미지(60~180, 200~290)·사각형(260~460, 200~290) 띠(y 190~300pt) 밖은 그대로 (0.5배 렌더 → 픽셀 y 95~150 밖)
        int diff = 0;
        for (int y = 0; y < 421; y++)
        {
            if (y is >= 93 and <= 152) continue;
            for (int x = 0; x < 298; x++)
            {
                int o = (y * 298 + x) * 4;
                if (Math.Abs(before[o] - after[o]) > 8 || Math.Abs(before[o + 1] - after[o + 1]) > 8 || Math.Abs(before[o + 2] - after[o + 2]) > 8) diff++;
            }
        }
        Assert.Equal(0, diff);
    }

    [Fact]
    public async Task Encrypted_ContentEdits_SavedStillEncrypted()
    {
        await using var s = await Open("pw1");
        await s.Commands.ExecuteAsync(await Edits(s));
        await new SaveService([]).SaveAsync(s, P("enc.pdf"));
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => _t.InvokeAsync(() => { using var d = PdfDocument.Open(_t, P("enc.pdf")); return 0; }));
        Assert.Equal(0, Inspect(P("enc.pdf"), "pw1").Images);
    }

    [Fact]
    public async Task Recovery_ContainsEdits()
    {
        await using var s = await Open();
        await s.Commands.ExecuteAsync(await Edits(s));
        var store = new RecoveryStore(P("rec"));
        await store.WriteAsync(s);
        var entry = Assert.Single(store.List());
        Assert.Equal(0, Inspect(entry.RecoveryPdfPath).Images);
    }

    [Fact]
    public async Task BuildIfNeeded_NoEditsNoPatch_ReturnsNull_WithEdits_WritesCopy()
    {
        await using var s = await Open();
        var b = new OutputCopyBuilder(null, null, null);
        Assert.Null(await b.BuildIfNeededAsync(s, P("none.pdf")));   // 인쇄·요약은 살아 있는 문서를 그대로 쓴다(OnlyAnnotations_NoContentStage)
        Assert.False(File.Exists(P("none.pdf")));
        await s.Commands.ExecuteAsync(await Edits(s));
        var copy = await b.BuildIfNeededAsync(s, P("some.pdf"));   // Print_ShowsMovedImage·Summary_PageImageShowsEdits가 쓰는 사본
        Assert.NotNull(copy);
        Assert.True(copy!.ContentApplied);
        Assert.Equal(0, Inspect(P("some.pdf")).Images);
    }
}
