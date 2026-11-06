using PdfEditor.App.Documents;

namespace PdfEditor.App.Tests.Hosting;

/// <summary>
/// AppHost 시험용: 설정·복구 폴더를 임시 폴더로 돌리고(환경 변수), 끝나면 환경 변수와 탭 모델의 정적 설정을 되돌린다.
/// </summary>
public sealed class HostTestScope : IDisposable
{
    private readonly string? _oldSettings = Environment.GetEnvironmentVariable("PDFEDITOR_SETTINGS_PATH");
    private readonly string? _oldRecovery = Environment.GetEnvironmentVariable("PDFEDITOR_RECOVERY_DIR");
    private readonly PdfEditor.App.Settings.SettingsStore? _statSettings = DocumentTabViewModel.Settings;
    private readonly PdfEditor.App.Shell.AppearanceServices? _statAppearance = DocumentTabViewModel.Appearance;

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "pdfeditor-host-" + Guid.NewGuid().ToString("N"));
    public string SettingsPath => Path.Combine(Dir, "settings.json");
    public string RecoveryDir => Path.Combine(Dir, "recovery");

    public HostTestScope()
    {
        Directory.CreateDirectory(Dir);
        Environment.SetEnvironmentVariable("PDFEDITOR_SETTINGS_PATH", SettingsPath);
        Environment.SetEnvironmentVariable("PDFEDITOR_RECOVERY_DIR", RecoveryDir);
    }

    public string File(string name) => Path.Combine(Dir, name);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PDFEDITOR_SETTINGS_PATH", _oldSettings);
        Environment.SetEnvironmentVariable("PDFEDITOR_RECOVERY_DIR", _oldRecovery);
        DocumentTabViewModel.Settings = _statSettings;
        DocumentTabViewModel.Appearance = _statAppearance;
        try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
