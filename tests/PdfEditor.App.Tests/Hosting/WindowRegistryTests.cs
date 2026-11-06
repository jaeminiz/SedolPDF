using Avalonia;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Hosting;
using PdfEditor.App.Settings;
using PdfEditor.App.Shell;
using PdfEditor.App.Tests.Documents;
using PdfEditor.Document.Saving;
using Xunit;

namespace PdfEditor.App.Tests.Hosting;

/// <summary>열린 창 목록과 마지막 활성 순서(0.8.0 여러 창, 단계 2). 화면 없이 가짜 창으로 시험한다.</summary>
public class WindowRegistryTests
{
    private sealed class FakeWindow(string name) : IHostWindow
    {
        public string Name { get; } = name;
        public MainWindowViewModel ViewModel { get; } = new();
        public bool IsClosed { get; set; }
        public int BroughtToFront { get; private set; }
        public void BringToFront() => BroughtToFront++;
        public PixelRect? TabStripScreenRect => null;
        public override string ToString() => Name;
    }

    [Fact]
    public void Add_MakesNewestLastActive()
    {
        var r = new WindowRegistry();
        var a = new FakeWindow("a");
        var b = new FakeWindow("b");
        r.Add(a);
        r.Add(b);
        Assert.Same(b, r.LastActive);
        Assert.Equal([b, a], r.ByRecent);
        Assert.Equal(2, r.Count);
    }

    [Fact]
    public void MarkActivated_MovesToFront()
    {
        var r = new WindowRegistry();
        var a = new FakeWindow("a");
        var b = new FakeWindow("b");
        var c = new FakeWindow("c");
        r.Add(a); r.Add(b); r.Add(c);
        r.MarkActivated(a);
        Assert.Equal([a, c, b], r.ByRecent);
        r.MarkActivated(new FakeWindow("unknown"));   // 목록에 없는 창은 무시
        Assert.Equal(3, r.Count);
    }

    [Fact]
    public void ByRecent_SkipsClosedWindows()
    {
        var r = new WindowRegistry();
        var a = new FakeWindow("a");
        var b = new FakeWindow("b");
        r.Add(a); r.Add(b);
        b.IsClosed = true;
        Assert.Equal([a], r.ByRecent);
        Assert.Same(a, r.LastActive);
        Assert.Equal(1, r.Count);
    }

    [Fact]
    public void Remove_LastActive_FallsBackToNext()
    {
        var r = new WindowRegistry();
        var a = new FakeWindow("a");
        var b = new FakeWindow("b");
        r.Add(a); r.Add(b);
        r.Remove(b);
        Assert.Same(a, r.LastActive);
        r.Remove(a);
        Assert.Null(r.LastActive);
        Assert.Equal(0, r.Count);
    }

    [Fact]
    public async Task FindOwner_ReturnsWindowHoldingSession()
    {
        using var t = new PdfiumThread();
        string dir = Path.Combine(Path.GetTempPath(), "pdfeditor-reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var wf = new FileWorkflow(t, new SaveService([]), new SettingsStore(Path.Combine(dir, "settings.json")), new FakePrompts());
        var s1 = await wf.NewAsync();
        var s2 = await wf.NewAsync();
        var s3 = await wf.NewAsync();
        try
        {
            var r = new WindowRegistry();
            var a = new FakeWindow("a");
            var b = new FakeWindow("b");
            r.Add(a); r.Add(b);
            a.ViewModel.Documents.Add(new DocumentTabViewModel(s1, s1.DisplayName, x => x()));
            b.ViewModel.Documents.Add(new DocumentTabViewModel(s2, s2.DisplayName, x => x()));
            Assert.Same(a, r.FindOwner(s1));
            Assert.Same(b, r.FindOwner(s2));
            Assert.Null(r.FindOwner(s3));
        }
        finally
        {
            await s1.DisposeAsync(); await s2.DisposeAsync(); await s3.DisposeAsync();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
