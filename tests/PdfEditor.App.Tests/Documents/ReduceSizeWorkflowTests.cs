using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.Document;
using PdfEditor.Document.Output;
using PdfEditor.Optimize;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Documents;

public sealed class FakeReduceDialogs : IReduceSizeDialogs
{
    public long? Target { get; set; }
    public NotAchievedChoice Choice { get; set; } = NotAchievedChoice.Cancel;
    public bool ConfirmRaster { get; set; } = true;
    public int AskTargetCalls { get; private set; }
    public long CurrentSeen { get; private set; }
    public List<(string Message, bool RasterAvailable)> NotAchieved { get; } = [];
    public int ConfirmCalls { get; private set; }
    public List<bool> ConfirmedMonochrome { get; } = [];

    public Task<long?> AskTargetBytesAsync(long currentBytes) { AskTargetCalls++; CurrentSeen = currentBytes; return Task.FromResult(Target); }
    public Task<NotAchievedChoice> AskNotAchievedAsync(string message, bool rasterAvailable) { NotAchieved.Add((message, rasterAvailable)); return Task.FromResult(Choice); }
    public Task<bool> ConfirmRasterizeAsync(bool monochrome) { ConfirmCalls++; ConfirmedMonochrome.Add(monochrome); return Task.FromResult(ConfirmRaster); }
}

public sealed class ReduceSizeWorkflowTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-rsw-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly FakePrompts _p = new();
    private readonly FakeReduceDialogs _ui = new();
    private readonly List<DocumentSession> _sessions = [];

    public Task InitializeAsync() { Directory.CreateDirectory(_dir); return Task.CompletedTask; }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions) await s.DisposeAsync();
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    private ReduceSizeWorkflow Wf() => new(_ui, _p, new SizeReducer(new OutputCopyBuilder(null, null, null)));

    private async Task<DocumentSession> OpenAsync(string path, string? password = null)
    {
        var s = await DocumentSession.OpenAsync(_t, path, password);
        _sessions.Add(s);
        return s;
    }

    private string NoisyPdf()
    {
        string src = P("noisy.pdf");
        ImagePdfFactory.Create(src,
            new ImageSpec(1200, 800, ImageKind.FlateRgb, 300, 200),
            new ImageSpec(1200, 800, ImageKind.FlateRgb, 300, 200));
        return src;
    }

    private async Task<DocumentSession> BlankAsync()
    {
        string src = P("blank.pdf");
        TestPdfFactory.CreateBlank(src, 1, 200, 200);
        return await OpenAsync(src);
    }

    [Fact]
    public async Task Encrypted_ShowsNoticeOnly()
    {
        string src = P("enc.pdf");
        TestPdfFactory.CreateEncrypted(src, 1, 200, 200, "pw");
        var s = await OpenAsync(src, "pw");
        await Wf().RunAsync(s);
        Assert.Equal([Strings.Msg_ReduceEncrypted], _p.Infos);
        Assert.Equal(0, _ui.AskTargetCalls);
    }

    [Fact]
    public async Task TargetDialogCancelled_DoesNothing()
    {
        var s = await OpenAsync(NoisyPdf());
        _ui.Target = null;
        await Wf().RunAsync(s);
        Assert.Equal(1, _ui.AskTargetCalls);
        Assert.Equal(0, _p.SavePickCalls);
        Assert.Equal(new FileInfo(P("noisy.pdf")).Length, _ui.CurrentSeen);
    }

    [Fact]
    public async Task SaveLocationCancelled_DoesNothing()
    {
        var s = await OpenAsync(NoisyPdf());
        _ui.Target = 1_000_000;
        await Wf().RunAsync(s);
        Assert.Equal(1, _p.SavePickCalls);
        Assert.Empty(_p.Infos);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task SameFileAsSource_IsRefused()
    {
        var s = await OpenAsync(NoisyPdf());
        _ui.Target = 1_000_000;
        _p.SavePicks.Enqueue(P("noisy.pdf"));
        await Wf().RunAsync(s);
        Assert.Equal([Strings.Msg_ReduceSameFile], _p.Infos);
        Assert.Equal(0, _p.ProgressRuns);
    }

    [Fact]
    public async Task Achieved_WritesFile_AndTellsTheSizes()
    {
        var s = await OpenAsync(NoisyPdf());
        _ui.Target = 1_000_000;
        string target = P("noisy_small.pdf");
        _p.SavePicks.Enqueue(target);
        await Wf().RunAsync(s);
        Assert.True(File.Exists(target));
        Assert.True(new FileInfo(target).Length <= 1_000_000);
        Assert.Single(_p.Infos);
        Assert.Contains(ByteSize.Format(new FileInfo(target).Length), _p.Infos[0]);
        Assert.Empty(_ui.NotAchieved);
        Assert.Single(_p.SuggestedNames);
        Assert.Contains("noisy", _p.SuggestedNames[0]);
    }

    [Fact]
    public async Task TargetAboveCurrent_SaysSoAndWritesNothing()
    {
        var s = await OpenAsync(NoisyPdf());
        _ui.Target = 2_000_000_000;
        string target = P("never.pdf");
        _p.SavePicks.Enqueue(target);
        await Wf().RunAsync(s);
        Assert.False(File.Exists(target));
        Assert.Single(_p.Infos);
    }

    [Fact]
    public async Task NotAchieved_KeepBestEffort_WritesFile()
    {
        var s = await BlankAsync();
        _ui.Target = 100;   // 빈 쪽 하나는 이보다 작아질 수 없다
        _ui.Choice = NotAchievedChoice.KeepBestEffort;
        string target = P("blank_small.pdf");
        _p.SavePicks.Enqueue(target);
        await Wf().RunAsync(s);
        Assert.True(File.Exists(target));
        var asked = Assert.Single(_ui.NotAchieved);
        Assert.True(asked.RasterAvailable);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task NotAchieved_Cancel_WritesNothing()
    {
        var s = await BlankAsync();
        _ui.Target = 100;
        _ui.Choice = NotAchievedChoice.Cancel;
        string target = P("c.pdf");
        _p.SavePicks.Enqueue(target);
        await Wf().RunAsync(s);
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task NotAchieved_RasterizeButNotConfirmed_WritesNothing()
    {
        var s = await BlankAsync();
        _ui.Target = 100;
        _ui.Choice = NotAchievedChoice.Rasterize;
        _ui.ConfirmRaster = false;
        string target = P("r.pdf");
        _p.SavePicks.Enqueue(target);
        await Wf().RunAsync(s);
        Assert.Equal(1, _ui.ConfirmCalls);
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Single(_ui.NotAchieved);
    }

    [Fact]
    public async Task NotAchieved_Rasterize_Confirmed_RunsRasterMode_AndAsksAgainWithoutRasterOption()
    {
        var s = await BlankAsync();
        _ui.Target = 100;   // 래스터화해도 100바이트는 못 맞춘다
        _ui.Choice = NotAchievedChoice.Rasterize;
        string target = P("raster.pdf");
        _p.SavePicks.Enqueue(target);
        // 두 번째 질문에서는 래스터 선택지가 없으므로 Rasterize를 고르면 취소와 같다 → 그 상태로 저장을 고르게 바꾼다.
        var wf = Wf();
        _ui.Choice = NotAchievedChoice.Rasterize;
        var run = wf.RunAsync(s);
        await run;
        Assert.Equal(2, _ui.NotAchieved.Count);
        Assert.True(_ui.NotAchieved[0].RasterAvailable);
        Assert.False(_ui.NotAchieved[1].RasterAvailable);
        Assert.Equal(1, _ui.ConfirmCalls);
        Assert.False(File.Exists(target));   // 두 번째 질문의 Rasterize는 선택지가 아니므로 저장하지 않는다
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task NotAchieved_Monochrome_Confirmed_AsksWithMonochromeWarning_AndRunsMonochromeMode()
    {
        var s = await BlankAsync();
        _ui.Target = 100;
        _ui.Choice = NotAchievedChoice.Monochrome;
        string target = P("mono.pdf");
        _p.SavePicks.Enqueue(target);
        await Wf().RunAsync(s);
        Assert.Equal([true], _ui.ConfirmedMonochrome);
        Assert.Equal(2, _ui.NotAchieved.Count);        // 흑백으로도 100바이트는 못 맞춰 다시 묻는다
        Assert.False(_ui.NotAchieved[1].RasterAvailable);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task NotAchieved_AfterRaster_KeepBestEffort_WritesRasterFile()
    {
        var s = await BlankAsync();
        _ui.Target = 100;
        string target = P("raster2.pdf");
        _p.SavePicks.Enqueue(target);
        var choices = new Queue<NotAchievedChoice>([NotAchievedChoice.Rasterize, NotAchievedChoice.KeepBestEffort]);
        var ui = new SequenceDialogs(_ui, choices);
        await new ReduceSizeWorkflow(ui, _p, new SizeReducer(new OutputCopyBuilder(null, null, null))).RunAsync(s);
        Assert.True(File.Exists(target));
        Assert.Equal(2, _ui.NotAchieved.Count);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    private sealed class SequenceDialogs(FakeReduceDialogs inner, Queue<NotAchievedChoice> choices) : IReduceSizeDialogs
    {
        public Task<long?> AskTargetBytesAsync(long currentBytes) => inner.AskTargetBytesAsync(currentBytes);
        public async Task<NotAchievedChoice> AskNotAchievedAsync(string message, bool rasterAvailable)
        {
            await inner.AskNotAchievedAsync(message, rasterAvailable);
            return choices.Dequeue();
        }
        public Task<bool> ConfirmRasterizeAsync(bool monochrome) => inner.ConfirmRasterizeAsync(monochrome);
    }
}
