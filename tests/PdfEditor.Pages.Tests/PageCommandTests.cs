using System.Text.RegularExpressions;
using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;

namespace PdfEditor.Pages.Tests;

public class PageCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-pages-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public PageCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    private async Task<DocumentSession> OpenLabeled(int n, string name = "l.pdf")
    {
        var p = P(name);
        // 쪽마다 크기를 다르게 해서 크기로도 구별
        TestPdfFactory.CreateLabeled(p, Enumerable.Range(0, n).Select(i => (300f + i * 10, 400f + i * 10)).ToList());
        return await DocumentSession.OpenAsync(_t, p, null);
    }

    private Task<string[]> Labels(DocumentSession s) => _t.InvokeAsync(() =>
    {
        var r = new List<string>();
        foreach (var info in s.Pages)
        {
            var tp = s.GetLoadedText(info.Id);
            var txt = tp.GetText(0, tp.CharCount);
            var m = Regex.Match(txt, @"PAGE (\d+)");
            r.Add(m.Success ? m.Groups[1].Value : "?");
        }
        return r.ToArray();
    });

    private Task<int> RotationOf(DocumentSession s, PageId id) => _t.InvokeAsync(() => s.GetLoadedPage(id).Rotation);

    private async Task AssertSnapshotMatches(DocumentSession s)
    {
        await _t.InvokeAsync(() =>
        {
            Assert.Equal(s.Document.PageCount, s.Pages.Length);
            for (int i = 0; i < s.Pages.Length; i++)
            {
                var sz = s.Document.GetPageSizePt(i);
                Assert.Equal(sz.Width, s.Pages[i].WidthPt, 0.5f);
                Assert.Equal(sz.Height, s.Pages[i].HeightPt, 0.5f);
            }
        });
    }

    [Fact]
    public async Task Delete_Undo_RestoresPageContentSizeAndId()
    {
        await using var s = await OpenLabeled(3);
        var ids = s.Pages.Select(x => x.Id).ToArray();
        await s.Commands.ExecuteAsync(new DeletePagesCommand([ids[1]]));
        Assert.Equal(2, s.Pages.Length);
        Assert.Equal(["1", "3"], await Labels(s));
        await AssertSnapshotMatches(s);

        await s.Commands.UndoAsync();
        Assert.Equal(3, s.Pages.Length);
        Assert.Equal(["1", "2", "3"], await Labels(s));
        Assert.Equal(ids, s.Pages.Select(x => x.Id).ToArray());
        Assert.Equal(310f, s.Pages[1].WidthPt, 0.5f);
        await AssertSnapshotMatches(s);

        await s.Commands.RedoAsync();
        Assert.Equal(["1", "3"], await Labels(s));
        await s.Commands.UndoAsync();
        Assert.Equal(["1", "2", "3"], await Labels(s));
    }

    [Fact]
    public async Task Delete_MultiplePages_Undo_RestoresOrder()
    {
        await using var s = await OpenLabeled(5);
        var ids = s.Pages.Select(x => x.Id).ToArray();
        await s.Commands.ExecuteAsync(new DeletePagesCommand([ids[3], ids[0], ids[1]]));
        Assert.Equal(["3", "5"], await Labels(s));
        await s.Commands.UndoAsync();
        Assert.Equal(["1", "2", "3", "4", "5"], await Labels(s));
        Assert.Equal(ids, s.Pages.Select(x => x.Id).ToArray());
        await AssertSnapshotMatches(s);
    }

    [Fact]
    public async Task Delete_Undo_KeepsAnnotationsAndRotation()
    {
        var p = P("a.pdf");
        TestPdfFactory.CreateLabeled(p, [(300, 400), (310, 410), (320, 420)]);
        TestPdfFactory.AddTextAnnotation(p, 1);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        var id = s.Pages[1].Id;
        await s.Commands.ExecuteAsync(new RotatePagesCommand([id], 1));
        await s.Commands.ExecuteAsync(new DeletePagesCommand([id]));
        await s.Commands.UndoAsync();
        await _t.InvokeAsync(() =>
        {
            var pg = s.GetLoadedPage(id);
            Assert.Equal(1, pg.AnnotationCount);
            Assert.Equal(1, pg.Rotation);
        });
    }

    [Fact]
    public async Task DeleteAllPages_Rejected_DocumentUnchanged()
    {
        await using var s = await OpenLabeled(3);
        var ids = s.Pages.Select(x => x.Id).ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Commands.ExecuteAsync(new DeletePagesCommand(ids)));
        Assert.Equal(3, s.Pages.Length);
        Assert.False(s.Commands.CanUndo);
        Assert.False(s.IsDirty);
        Assert.Equal(["1", "2", "3"], await Labels(s));
        await AssertSnapshotMatches(s);
    }

    [Fact]
    public void Delete_EmptyTarget_Throws() =>
        Assert.Throws<ArgumentException>(() => new DeletePagesCommand([]));

    [Fact]
    public async Task Rotate_Undo_Redo()
    {
        await using var s = await OpenLabeled(2);
        var id = s.Pages[0].Id;
        await s.Commands.ExecuteAsync(new RotatePagesCommand([id], 1));
        await s.Commands.ExecuteAsync(new RotatePagesCommand([id], 1));
        Assert.Equal(2, await RotationOf(s, id));
        await s.Commands.UndoAsync();
        Assert.Equal(1, await RotationOf(s, id));
        await s.Commands.RedoAsync();
        Assert.Equal(2, await RotationOf(s, id));

        var id2 = s.Pages[1].Id;
        await s.Commands.ExecuteAsync(new RotatePagesCommand([id2], -1));
        Assert.Equal(3, await RotationOf(s, id2));
        await s.Commands.UndoAsync();
        Assert.Equal(0, await RotationOf(s, id2));
    }

    [Fact]
    public async Task RotateDeleteUndo_NeverReusesEarlierVersion()
    {
        // (PageId, Version)는 그 쪽 모양 하나만 가리켜야 한다(Review Focus 1). 회전 → 삭제 → 되살리기 뒤
        // 버전이 0으로 돌아가면 회전 전 v0 타일·썸네일(늦게 도착한 결과 포함)이 회전된 쪽에 맞는 것으로 통과된다.
        await using var s = await OpenLabeled(3);
        var id = s.Pages[1].Id;
        var seen = new HashSet<int> { s.Pages[1].Version };
        await s.Commands.ExecuteAsync(new RotatePagesCommand([id], 1));
        Assert.True(seen.Add(s.Pages[1].Version));
        await s.Commands.ExecuteAsync(new DeletePagesCommand([id]));
        await s.Commands.UndoAsync();   // 되살림 (회전된 상태)
        Assert.Equal(id, s.Pages[1].Id);
        Assert.Equal(1, await RotationOf(s, id));
        Assert.True(seen.Add(s.Pages[1].Version), "되살린 쪽이 예전 버전 번호를 다시 썼다");
        await s.Commands.RedoAsync();   // 다시 삭제
        await s.Commands.UndoAsync();   // 다시 되살림
        Assert.True(seen.Add(s.Pages[1].Version), "다시 되살린 쪽이 예전 버전 번호를 다시 썼다");
    }

    [Fact]
    public async Task ImportUndoRedo_NeverReusesEarlierVersion()
    {
        await using var s = await OpenLabeled(2);
        var other = P("o.pdf");
        TestPdfFactory.CreateBlank(other, 1, 100, 100);
        var src = await _t.InvokeAsync(() => PdfDocument.Open(_t, other));
        await s.Commands.ExecuteAsync(new PdfEditor.Pages.ImportPagesCommand(src, [0], 1));
        var first = s.Pages[1];
        await s.Commands.UndoAsync();
        await s.Commands.RedoAsync();
        Assert.Equal(first.Id, s.Pages[1].Id);
        Assert.NotEqual(first.Version, s.Pages[1].Version);
    }

    [Fact]
    public async Task Rotate_BumpsVersion_AndSwapsSize()
    {
        await using var s = await OpenLabeled(2);
        var id = s.Pages[0].Id;
        int v0 = s.Pages[0].Version;
        float w0 = s.Pages[0].WidthPt, h0 = s.Pages[0].HeightPt;
        await s.Commands.ExecuteAsync(new RotatePagesCommand([id], 1));
        Assert.True(s.Pages[0].Version > v0);
        Assert.Equal(h0, s.Pages[0].WidthPt, 0.5f);
        Assert.Equal(w0, s.Pages[0].HeightPt, 0.5f);
        await AssertSnapshotMatches(s);
        await s.Commands.UndoAsync();
        Assert.Equal(w0, s.Pages[0].WidthPt, 0.5f);
        await AssertSnapshotMatches(s);
    }

    [Fact]
    public async Task Move_Undo_RestoresOrder()
    {
        await using var s = await OpenLabeled(5);
        var ids = s.Pages.Select(x => x.Id).ToArray();
        await s.Commands.ExecuteAsync(new MovePagesCommand([ids[2], ids[3]], 0));
        Assert.Equal(["3", "4", "1", "2", "5"], await Labels(s));
        Assert.Equal(new[] { ids[2], ids[3], ids[0], ids[1], ids[4] }, s.Pages.Select(x => x.Id).ToArray());
        await AssertSnapshotMatches(s);
        await s.Commands.UndoAsync();
        Assert.Equal(["1", "2", "3", "4", "5"], await Labels(s));
        Assert.Equal(ids, s.Pages.Select(x => x.Id).ToArray());
        await s.Commands.RedoAsync();
        Assert.Equal(["3", "4", "1", "2", "5"], await Labels(s));
    }

    [Fact]
    public async Task ReorderTo_RandomPermutations_MatchesTarget()
    {
        await using var s = await OpenLabeled(8);
        var ids = s.Pages.Select(x => x.Id).ToArray();
        var rnd = new Random(12345);
        for (int k = 0; k < 20; k++)
        {
            var target = ids.OrderBy(_ => rnd.Next()).ToArray();
            await _t.InvokeAsync(() => { s.ReleasePageHandles(); PageOrder.ReorderTo(s, target); s.Publish(); });
            Assert.Equal(target, s.Pages.Select(x => x.Id).ToArray());
            var expected = target.Select(id => (Array.IndexOf(ids, id) + 1).ToString()).ToArray();
            Assert.Equal(expected, await Labels(s));
            await AssertSnapshotMatches(s);
        }
    }

    [Fact]
    public async Task InsertBlank_Undo_Redo()
    {
        await using var s = await OpenLabeled(2);
        await s.Commands.ExecuteAsync(new InsertBlankPageCommand(1, 200, 100));
        Assert.Equal(3, s.Pages.Length);
        Assert.Equal(200f, s.Pages[1].WidthPt, 0.5f);
        Assert.Equal(100f, s.Pages[1].HeightPt, 0.5f);
        await AssertSnapshotMatches(s);
        var id = s.Pages[1].Id;
        await s.Commands.UndoAsync();
        Assert.Equal(["1", "2"], await Labels(s));
        await s.Commands.RedoAsync();
        Assert.Equal(3, s.Pages.Length);
        Assert.Equal(id, s.Pages[1].Id);
        await AssertSnapshotMatches(s);
    }

    [Fact]
    public async Task Import_Undo_Redo()
    {
        await using var s = await OpenLabeled(3);
        var other = P("o.pdf");
        TestPdfFactory.CreateBlank(other, 2, 123, 234);
        var src = await _t.InvokeAsync(() => PdfDocument.Open(_t, other));
        var cmd = new ImportPagesCommand(src, [0, 1], 1);
        await s.Commands.ExecuteAsync(cmd);
        Assert.Equal(5, s.Pages.Length);
        Assert.Equal(123f, s.Pages[1].WidthPt, 0.5f);
        Assert.Equal(234f, s.Pages[2].HeightPt, 0.5f);
        await AssertSnapshotMatches(s);
        var ids = s.Pages.Select(x => x.Id).ToArray();
        await s.Commands.UndoAsync();
        Assert.Equal(3, s.Pages.Length);
        Assert.Equal(["1", "2", "3"], await Labels(s));
        await AssertSnapshotMatches(s);
        await s.Commands.RedoAsync();
        Assert.Equal(5, s.Pages.Length);
        Assert.Equal(ids, s.Pages.Select(x => x.Id).ToArray());
        await AssertSnapshotMatches(s);
    }

    [Fact]
    public async Task Import_FromAnnotatedFile_OriginalsAttachedToNewPages()
    {
        await using var s = await OpenLabeled(2);
        var other = P("ann.pdf");
        AnnotatedPdfFactory.CreateMixed(other);
        var bytes = File.ReadAllBytes(other);
        var src = await _t.InvokeAsync(() => PdfDocument.OpenBytes(_t, bytes));
        var originals = await OriginalAnnotationLoader.ReadAsync(_t, src, bytes, null, [0]);
        Assert.NotNull(originals);
        await s.Commands.ExecuteAsync(new ImportPagesCommand(src, [0], 1, originals));
        Assert.Equal(3, s.Pages.Length);
        Assert.Equal(9, s.Annotations.Get(s.Pages[1].Id).Items.Length);
        Assert.Empty(s.Annotations.Get(s.Pages[0].Id).Items);
    }

    [Fact]
    public async Task Import_DisposesSourceWhenSessionDisposed()
    {
        var s = await OpenLabeled(1);
        var other = P("o2.pdf");
        TestPdfFactory.CreateBlank(other, 1, 100, 100);
        var src = await _t.InvokeAsync(() => PdfDocument.Open(_t, other));
        await s.Commands.ExecuteAsync(new ImportPagesCommand(src, [0], 1));
        await s.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _t.InvokeAsync(() => src.PageCount));
    }

    [Fact]
    public async Task MixedSequence_SnapshotAlwaysMatchesPdfium()
    {
        await using var s = await OpenLabeled(4);
        var ids = s.Pages.Select(x => x.Id).ToArray();
        await s.Commands.ExecuteAsync(new RotatePagesCommand([ids[0], ids[2]], 1));
        await AssertSnapshotMatches(s);
        await s.Commands.ExecuteAsync(new DeletePagesCommand([ids[1]]));
        await AssertSnapshotMatches(s);
        await s.Commands.ExecuteAsync(new MovePagesCommand([ids[3]], 0));
        await AssertSnapshotMatches(s);
        await s.Commands.ExecuteAsync(new InsertBlankPageCommand(2, 50, 60));
        await AssertSnapshotMatches(s);
        while (s.Commands.CanUndo) { await s.Commands.UndoAsync(); await AssertSnapshotMatches(s); }
        Assert.Equal(["1", "2", "3", "4"], await Labels(s));
        Assert.False(s.IsDirty);
        while (s.Commands.CanRedo) { await s.Commands.RedoAsync(); await AssertSnapshotMatches(s); }
    }
}
