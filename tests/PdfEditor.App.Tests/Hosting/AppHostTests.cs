using PdfEditor.App.Documents;
using PdfEditor.App.Hosting;
using Xunit;

namespace PdfEditor.App.Tests.Hosting;

/// <summary>앱 공유 서비스(PDFium 스레드·설정·파일 작업·복구)를 창 밖의 AppHost가 갖는다(0.8.0 여러 창, 단계 1).</summary>
public sealed class AppHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-apphost-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldSettings = Environment.GetEnvironmentVariable("PDFEDITOR_SETTINGS_PATH");
    private readonly string? _oldRecovery = Environment.GetEnvironmentVariable("PDFEDITOR_RECOVERY_DIR");
    private readonly SettingsStoreSnapshot _statics = new(DocumentTabViewModel.Settings, DocumentTabViewModel.Appearance);

    private sealed record SettingsStoreSnapshot(PdfEditor.App.Settings.SettingsStore? Settings, PdfEditor.App.Shell.AppearanceServices? Appearance);

    public AppHostTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("PDFEDITOR_SETTINGS_PATH", SettingsPath);
        Environment.SetEnvironmentVariable("PDFEDITOR_RECOVERY_DIR", Path.Combine(_dir, "recovery"));
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PDFEDITOR_SETTINGS_PATH", _oldSettings);
        Environment.SetEnvironmentVariable("PDFEDITOR_RECOVERY_DIR", _oldRecovery);
        DocumentTabViewModel.Settings = _statics.Settings;
        DocumentTabViewModel.Appearance = _statics.Appearance;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Create_WiresSharedServices()
    {
        using var h = new AppHost(AppContext.BaseDirectory, () => null!);
        Assert.NotNull(h.Workflow);
        Assert.NotNull(h.Recovery);
        Assert.NotNull(h.Prompts);
        Assert.Equal(SettingsPath, h.Paths.SettingsPath);
        Assert.Same(h.Settings, DocumentTabViewModel.Settings);
        Assert.Same(h.Services, DocumentTabViewModel.Appearance);
    }

    [Fact]
    public async Task Workflow_UsesHostPdfiumThread()
    {
        var h = new AppHost(AppContext.BaseDirectory, () => null!);
        var s = await h.Workflow.NewAsync();
        Assert.Single(h.Workflow.OpenSessions);
        h.Workflow.Release(s);
        await s.DisposeAsync();
        h.Dispose();
    }

    [Fact]
    public void Dispose_Twice_IsSafe()
    {
        var h = new AppHost(AppContext.BaseDirectory, () => null!);
        h.Dispose();
        h.Dispose();
    }
}
