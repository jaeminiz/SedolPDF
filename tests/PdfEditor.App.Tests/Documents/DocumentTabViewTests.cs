using Avalonia;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Viewer;
using PdfEditor.Document;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Documents;

public class DocumentTabViewTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-tv-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private DocumentSession _s = null!;
    private DocumentTabViewModel _tab = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "a.pdf");
        TestPdfFactory.CreateLabeled(path, [(400, 600), (400, 600), (400, 600)]);
        _s = await DocumentSession.OpenAsync(_t, path, null);
        _tab = new DocumentTabViewModel(_s, "a.pdf", null);
    }

    public async Task DisposeAsync()
    {
        _tab.Detach();
        await _s.DisposeAsync();
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void ZoomInOut_UsesZoomMathSteps()
    {
        _tab.Scale = 1.0;
        _tab.ZoomIn(); Assert.Equal(1.25, _tab.Scale, 6);
        _tab.ZoomOut(); _tab.ZoomOut(); Assert.Equal(0.75, _tab.Scale, 6);
    }

    [Fact]
    public void FitWidth_And_FitPage_UseCurrentPageAndViewport()
    {
        _tab.ViewportSize = new Size(832, 700);
        _tab.FitWidth();
        Assert.Equal((832 - 2 * PageLayout.MarginPx) / (400 * ZoomMath.PointsToDip), _tab.Scale, 6);
        _tab.FitPage();
        Assert.Equal((700 - 2 * PageLayout.MarginPx) / (600 * ZoomMath.PointsToDip), _tab.Scale, 6);
        _tab.ActualSize();
        Assert.Equal(1.0, _tab.Scale, 6);
    }

    [Fact]
    public void PageNavigation_ClampsToRange()
    {
        _tab.LastPage(); Assert.Equal(2, _tab.CurrentPageIndex);
        _tab.NextPage(); Assert.Equal(2, _tab.CurrentPageIndex);
        _tab.PrevPage(); Assert.Equal(1, _tab.CurrentPageIndex);
        _tab.FirstPage(); Assert.Equal(0, _tab.CurrentPageIndex);
        _tab.PrevPage(); Assert.Equal(0, _tab.CurrentPageIndex);
    }

    [Fact]
    public void Cursor_FormatsOnlyWhenOnPage()
    {
        Assert.Null(_tab.CursorXmm);
        _tab.SetCursorFromPagePoint(new Point(72, 36), 1.0);   // 72pt = 25.4mm
        Assert.Equal(25.4, _tab.CursorXmm!.Value, 6);
        Assert.Equal(12.7, _tab.CursorYmm!.Value, 6);
        _tab.SetCursorFromPagePoint(null, 1.0);
        Assert.Null(_tab.CursorXmm);
    }

    [Fact]
    public void Detach_RaisesDetached()
    {
        bool raised = false;
        _tab.Detached += () => raised = true;
        _tab.Detach();
        Assert.True(raised);
    }
}
