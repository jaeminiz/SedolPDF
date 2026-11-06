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
    public NotAchievedDecision Decision { get; set; } = new(NotAchievedKind.Cancel);
    /// <summary>비어 있지 않으면 질문마다 하나씩 꺼내 쓴다(두 번째 질문 등). 비면 Decision.</summary>
    public Queue<NotAchievedDecision> Decisions { get; } = new();
    public int AskTargetCalls { get; private set; }
    public long CurrentSeen { get; private set; }
    public List<(string Message, long BestEffortBytes, long TargetBytes, ConversionEstimates? Estimates)> NotAchieved { get; } = [];

    public Task<long?> AskTargetBytesAsync(long currentBytes) { AskTargetCalls++; CurrentSeen = currentBytes; return Task.FromResult(Target); }
    public Task<NotAchievedDecision> AskNotAchievedAsync(string message, long bestEffortBytes, long targetBytes, ConversionEstimates? estimates)
    {
        NotAchieved.Add((message, bestEffortBytes, targetBytes, estimates));
        return Task.FromResult(Decisions.Count > 0 ? Decisions.Dequeue() : Decision);
    }
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
    public async Task NotAchieved_KeepBestEffort_WritesFile_AndShowedEstimatesUpFront()
    {
        var s = await BlankAsync();
        _ui.Target = 100;   // 빈 쪽 하나는 이보다 작아질 수 없다
        _ui.Decision = new(NotAchievedKind.KeepBestEffort);
        string target = P("blank_small.pdf");
        _p.SavePicks.Enqueue(target);
        await Wf().RunAsync(s);
        Assert.True(File.Exists(target));
        var asked = Assert.Single(_ui.NotAchieved);
        Assert.NotNull(asked.Estimates);   // 변환 예상 용량이 선택 창에 함께 전달된다
        Assert.Equal([600, 300, 200], asked.Estimates!.Layered.Select(g => g.Dpi).ToArray());
        Assert.Equal(100, asked.TargetBytes);
        Assert.Equal(new FileInfo(target).Length, asked.BestEffortBytes);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task NotAchieved_Cancel_WritesNothing()
    {
        var s = await BlankAsync();
        _ui.Target = 100;
        _ui.Decision = new(NotAchievedKind.Cancel);
        string target = P("c.pdf");
        _p.SavePicks.Enqueue(target);
        await Wf().RunAsync(s);
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task NotAchieved_Layered_SavesTheChosenGrade_WithoutAskingAgain()
    {
        var s = await BlankAsync();
        _ui.Target = 100;
        _ui.Decision = new(NotAchievedKind.Layered, 1);   // 중간
        string target = P("layered.pdf");
        _p.SavePicks.Enqueue(target);
        await Wf().RunAsync(s);

        Assert.Single(_ui.NotAchieved);
        Assert.True(File.Exists(target));
        var info = Assert.Single(_p.Infos);
        Assert.Contains(ByteSize.Format(new FileInfo(target).Length), info);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task NotAchieved_ColorImages_RunsRasterMode_AndAsksAgainWithoutConversions()
    {
        var s = await BlankAsync();
        _ui.Target = 100;   // 변환해도 100바이트는 못 맞춘다
        _ui.Decisions.Enqueue(new(NotAchievedKind.ColorImages));
        _ui.Decisions.Enqueue(new(NotAchievedKind.KeepBestEffort));
        string target = P("raster.pdf");
        _p.SavePicks.Enqueue(target);
        await Wf().RunAsync(s);

        Assert.Equal(2, _ui.NotAchieved.Count);
        Assert.NotNull(_ui.NotAchieved[0].Estimates);
        Assert.Null(_ui.NotAchieved[1].Estimates);   // 두 번째 질문에는 변환 선택지가 없다
        Assert.True(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task NotAchieved_ConversionChosenOnTheSecondQuestion_IsIgnored()
    {
        var s = await BlankAsync();
        _ui.Target = 100;
        _ui.Decisions.Enqueue(new(NotAchievedKind.ColorImages));
        _ui.Decisions.Enqueue(new(NotAchievedKind.Layered, 0));   // 변환 선택지가 없는 질문에서는 취소와 같다
        string target = P("raster3.pdf");
        _p.SavePicks.Enqueue(target);
        await Wf().RunAsync(s);
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
}
