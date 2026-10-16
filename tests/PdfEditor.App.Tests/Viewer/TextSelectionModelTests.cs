using PdfEditor.App.Tests.Panels;
using PdfEditor.App.Viewer;
using PdfEditor.Document;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Viewer;

public class TextSelectionModelTests : PanelTestBase
{
    private sealed class FakeClipboard : IClipboardService
    {
        public List<string> Texts { get; } = [];
        public Task SetTextAsync(string text) { Texts.Add(text); return Task.CompletedTask; }
    }

    [Fact]
    public void Selection_ReversedDrag_NormalizesRange()
    {
        var m = new TextSelectionModel();
        m.Begin(new PageId(1), 10);
        m.Extend(4);
        Assert.Equal((4, 7), m.Range);
        m.Extend(12);
        Assert.Equal((10, 3), m.Range);
        m.Extend(10);
        Assert.Equal((10, 1), m.Range);   // 한 글자
    }

    [Fact]
    public void Selection_ClearAndBegin_Reset()
    {
        var m = new TextSelectionModel();
        Assert.Null(m.Range);
        m.Begin(new PageId(1), 3);
        m.Extend(5);
        m.Clear();
        Assert.Null(m.Range);
        Assert.Null(m.Page);
        m.Begin(new PageId(2), 7);
        Assert.Equal(new PageId(2), m.Page);
        Assert.Equal((7, 1), m.Range);
    }

    [Fact]
    public void Selection_RaisesChanged()
    {
        var m = new TextSelectionModel();
        int n = 0;
        m.Changed += () => n++;
        m.Begin(new PageId(1), 1);
        m.Extend(2);
        m.Clear();
        Assert.Equal(3, n);
    }

    [Fact]
    public async Task Copy_Restricted_DoesNotTouchClipboard()
    {
        string p = Path.Combine(Dir, "r.pdf");
        TestPdfFactory.CreateOwnerRestricted(p, allowPrint: true, allowModify: true, allowCopy: false);
        var tab = await OpenTab(p);
        var sel = new TextSelectionModel();
        sel.Begin(tab.Session!.Pages[0].Id, 0);
        sel.Extend(3);
        var cb = new FakeClipboard();

        var r = await TextCopier.CopyAsync(tab.Session, sel, cb);

        Assert.Equal(CopyResult.Restricted, r);
        Assert.Empty(cb.Texts);
    }

    [Fact]
    public async Task Copy_Allowed_PutsSelectedTextOnClipboard()
    {
        string p = Path.Combine(Dir, "c.pdf");
        TestPdfFactory.CreateLabeled(p, [(595, 842)]);
        var tab = await OpenTab(p);
        var sel = new TextSelectionModel();
        sel.Begin(tab.Session!.Pages[0].Id, 0);
        sel.Extend(3);
        var cb = new FakeClipboard();

        var r = await TextCopier.CopyAsync(tab.Session, sel, cb);

        Assert.Equal(CopyResult.Copied, r);
        Assert.Equal("PAGE", Assert.Single(cb.Texts));
    }

    [Fact]
    public async Task Copy_NothingSelected_ReturnsNothing()
    {
        string p = Path.Combine(Dir, "n.pdf");
        TestPdfFactory.CreateLabeled(p, [(595, 842)]);
        var tab = await OpenTab(p);
        var cb = new FakeClipboard();
        Assert.Equal(CopyResult.NothingSelected, await TextCopier.CopyAsync(tab.Session!, new TextSelectionModel(), cb));
        Assert.Empty(cb.Texts);
    }
}
