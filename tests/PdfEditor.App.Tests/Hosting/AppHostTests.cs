using PdfEditor.App.Documents;
using PdfEditor.App.Hosting;
using Xunit;

namespace PdfEditor.App.Tests.Hosting;

/// <summary>앱 공유 서비스(PDFium 스레드·설정·파일 작업·복구)를 창 밖의 AppHost가 갖는다(0.8.0 여러 창, 단계 1).</summary>
public sealed class AppHostTests : IDisposable
{
    private readonly HostTestScope _scope = new();

    public void Dispose() => _scope.Dispose();

    [Fact]
    public void Create_WiresSharedServices()
    {
        using var h = new AppHost(AppContext.BaseDirectory);
        Assert.NotNull(h.Workflow);
        Assert.NotNull(h.Recovery);
        Assert.NotNull(h.Prompts);
        Assert.Equal(_scope.SettingsPath, h.Paths.SettingsPath);
        Assert.Same(h.Settings, DocumentTabViewModel.Settings);
        Assert.Same(h.Services, DocumentTabViewModel.Appearance);
        Assert.Equal(0, h.Registry.Count);
    }

    [Fact]
    public async Task Workflow_UsesHostPdfiumThread()
    {
        var h = new AppHost(AppContext.BaseDirectory);
        var s = await h.Workflow.NewAsync();
        Assert.Single(h.Workflow.OpenSessions);
        h.Workflow.Release(s);
        await s.DisposeAsync();
        h.Dispose();
    }

    [Fact]
    public void Dispose_Twice_IsSafe()
    {
        var h = new AppHost(AppContext.BaseDirectory);
        h.Dispose();
        h.Dispose();
    }
}
