using System.Security.Cryptography;
using Pdf.Native;
using PdfEditor.TestFixtures;

namespace PdfEditor.Document.Tests;

public class DocumentSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-session-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public DocumentSessionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task Open_PagesSnapshot_MatchesPdfium()
    {
        var p = P("l.pdf");
        TestPdfFactory.CreateLabeled(p, [(595, 842), (842, 595), (300, 400)]);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);

        Assert.Equal(3, s.Pages.Length);
        Assert.Equal(3, s.Pages.Select(x => x.Id).Distinct().Count());
        Assert.Equal(595f, s.Pages[0].WidthPt, 0.5f);
        Assert.Equal(842f, s.Pages[1].WidthPt, 0.5f);
        Assert.Equal(400f, s.Pages[2].HeightPt, 0.5f);
        Assert.Equal("l.pdf", s.DisplayName);
        Assert.Equal(p, s.FilePath);
    }

    [Fact]
    public async Task Open_ComputesSha256()
    {
        var p = P("h.pdf");
        TestPdfFactory.CreateBlank(p, 1, 595, 842);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(p)), s.OriginalSha256);
    }

    [Fact]
    public async Task Open_WrongPassword_PropagatesPasswordException()
    {
        var p = P("enc.pdf");
        TestPdfFactory.CreateEncrypted(p, 1, 595, 842, "pw1");
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => DocumentSession.OpenAsync(_t, p, "bad"));
        await using var s = await DocumentSession.OpenAsync(_t, p, "pw1");
        Assert.True(s.IsEncrypted);
        Assert.Equal("pw1", s.Password);
    }

    [Fact]
    public async Task Open_Repaired_SetsWasRepaired()
    {
        var p = P("c.pdf");
        TestPdfFactory.CreateBlank(p, 2, 595, 842);
        TestPdfFactory.CorruptXref(p);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        Assert.True(s.WasRepaired);
        Assert.Equal(2, s.Pages.Length);

        var ok = P("ok.pdf");
        TestPdfFactory.CreateBlank(ok, 1, 595, 842);
        await using var s2 = await DocumentSession.OpenAsync(_t, ok, null);
        Assert.False(s2.WasRepaired);
    }

    [Fact]
    public async Task CreateNew_OnePageA4_NotDirty()
    {
        await using var s = await DocumentSession.CreateNewAsync(_t);
        Assert.Single(s.Pages);
        Assert.Equal(595.28f, s.Pages[0].WidthPt, 0.5f);
        Assert.Equal(841.89f, s.Pages[0].HeightPt, 0.5f);
        Assert.False(s.IsDirty);
        Assert.Null(s.FilePath);
    }

    [Fact]
    public async Task CreateEmpty_ZeroPages()
    {
        await using var s = await DocumentSession.CreateEmptyAsync(_t);
        Assert.Empty(s.Pages);
    }

    [Fact]
    public async Task IndexOf_AfterMove_FollowsPage()
    {
        var p = P("m.pdf");
        TestPdfFactory.CreateLabeled(p, [(100, 100), (200, 100), (300, 100), (400, 100)]);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        var ids = s.Pages.Select(x => x.Id).ToArray();

        await _t.InvokeAsync(() =>
        {
            s.Document.MovePages([0], 2);
            s.MovePageIds([ids[0]], 2);
            s.Publish();
        });

        Assert.Equal(2, await _t.InvokeAsync(() => s.IndexOf(ids[0])));
        Assert.Equal(0, await _t.InvokeAsync(() => s.IndexOf(ids[1])));
        Assert.Equal([ids[1], ids[2], ids[0], ids[3]], s.Pages.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task MovePageIds_MultiplePages_MatchesPdfiumMovePages()
    {
        var p = P("mm.pdf");
        TestPdfFactory.CreateLabeled(p, [(100, 100), (200, 100), (300, 100), (400, 100)]);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        var ids = s.Pages.Select(x => x.Id).ToArray();

        await _t.InvokeAsync(() =>
        {
            s.Document.MovePages([0, 2], 1);
            s.MovePageIds([ids[0], ids[2]], 1);
            s.Publish();
            // PageId 순서가 PDFium의 실제 순서와 같은지 (크기 캐시를 거치지 않고) 확인
            for (int i = 0; i < s.Pages.Length; i++)
                Assert.Equal(s.Document.GetPageSizePt(i).Width, s.Pages[i].WidthPt, 0.5f);
        });
        Assert.Equal([ids[1], ids[0], ids[2], ids[3]], s.Pages.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task Publish_RaisesPagesChanged_WithStructureFlag()
    {
        await using var s = await DocumentSession.CreateNewAsync(_t);
        var events = new List<PagesChangedEventArgs>();
        s.PagesChanged += events.Add;

        var id0 = s.Pages[0].Id;
        await _t.InvokeAsync(() => { s.BumpVersion(id0); s.Publish(); });
        Assert.Single(events);
        Assert.False(events[0].StructureChanged);
        Assert.Contains(id0, events[0].Changed);
        Assert.Equal(1, s.Pages[0].Version);

        await _t.InvokeAsync(() =>
        {
            var ids = s.NewPageIds(1);
            s.Document.InsertBlankPage(1, 100, 100);
            s.InsertPageIds(1, ids);
            s.Publish();
        });
        Assert.Equal(2, events.Count);
        Assert.True(events[1].StructureChanged);
        Assert.Equal(2, s.Pages.Length);

        await _t.InvokeAsync(() =>
        {
            s.Document.DeletePage(1);
            s.RemovePageIdAt(1);
            s.Publish();
        });
        Assert.True(events[2].StructureChanged);
        Assert.Single(s.Pages);
    }

    [Fact]
    public async Task GetLoadedPage_ReusesHandle_UntilReleased()
    {
        var p = P("r.pdf");
        TestPdfFactory.CreateBlank(p, 2, 595, 842);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        var id = s.Pages[0].Id;
        await _t.InvokeAsync(() =>
        {
            var a = s.GetLoadedPage(id);
            var b = s.GetLoadedPage(id);
            Assert.Same(a, b);
            var t1 = s.GetLoadedText(id);
            Assert.Same(t1, s.GetLoadedText(id));
            s.ReleasePageHandles();
            var c = s.GetLoadedPage(id);
            Assert.NotSame(a, c);
        });
    }

    [Fact]
    public async Task GetLoadedPage_EvictsBeyondCapacity_AndStaysUsable()
    {
        var p = P("many.pdf");
        TestPdfFactory.CreateBlank(p, 12, 595, 842);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        await _t.InvokeAsync(() =>
        {
            foreach (var info in s.Pages)
            {
                var pg = s.GetLoadedPage(info.Id);
                Assert.True(pg.SizePt.Width > 0);
                _ = s.GetLoadedText(info.Id).CharCount;
            }
            // 가장 오래된 쪽은 밀려났으므로 다시 불러도 동작해야 한다
            Assert.True(s.GetLoadedPage(s.Pages[0].Id).SizePt.Width > 0);
        });
    }

    [Fact]
    public async Task Dispose_ClosesDocumentAndStash()
    {
        var s = await DocumentSession.CreateNewAsync(_t);
        await _t.InvokeAsync(() => { _ = s.UndoStash; });
        await s.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => s.Document);
        Assert.Throws<ObjectDisposedException>(() => s.UndoStash);
        await s.DisposeAsync(); // 두 번 불러도 안전
    }
}
