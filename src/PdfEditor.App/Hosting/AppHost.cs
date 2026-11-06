using Avalonia.Controls;
using Pdf.Native;
using PdfEditor.App.Dialogs;
using PdfEditor.App.Documents;
using PdfEditor.App.Settings;
using PdfEditor.App.Shell;
using PdfEditor.Document.Recovery;
using PdfEditor.Document.Saving;

namespace PdfEditor.App.Hosting;

/// <summary>
/// 앱에 하나인 공유 서비스(0.8.0 여러 창 설계 4.1). PDFium 스레드·설정·열린 세션 목록(FileWorkflow)·복구를 갖는다.
/// 세션은 PDFium 스레드에 묶여 있으므로 이 스레드를 앱 하나로 두어야 세션을 창 사이로 옮길 수 있다.
/// 창마다 다른 것(인쇄·용량 줄이기 대화상자, 패널 상태)은 창이 만든다.
/// </summary>
public sealed class AppHost : IDisposable
{
    private bool _disposed;

    /// <param name="dialogOwner">대화상자를 띄울 창. 띄우는 순간 부른다.</param>
    public AppHost(string baseDirectory, Func<Window> dialogOwner)
    {
        Prompts = new AvaloniaPrompts(dialogOwner);
        // 한글 글꼴(Noto Sans KR)은 앱 출력 폴더의 assets/fonts에서 한 번 읽어 AP·패치·화면이 함께 쓴다. 없으면 글꼴이 필요한 주석에서 예외.
        Services = AppearanceServices.Create(baseDirectory);
        DocumentTabViewModel.Appearance = Services;
        Paths = AppPaths.Resolve(baseDirectory, AppPaths.CanWriteFolder);
        Settings = new SettingsStore(Paths.SettingsPath);
        DocumentTabViewModel.Settings = Settings;
        var appearance = Services.Appearance;
        var patcher = Services.Patcher;
        Workflow = new FileWorkflow(Pdfium, new SaveService([], appearance, patcher, Services.Assets), Settings, Prompts);
        Recovery = new RecoveryCoordinator(Pdfium, new RecoveryStore(Paths.RecoveryFolder, appearance, patcher, Services.Assets), Settings,
            new AvaloniaRecoveryUi(dialogOwner), Prompts);
        Recovery.Attach(Workflow);
    }

    public PdfiumThread Pdfium { get; } = new();   // 앱 전체에서 PdfiumThread는 하나
    public AppearanceServices Services { get; }
    public AppPaths Paths { get; }
    public SettingsStore Settings { get; }
    public IUserPrompts Prompts { get; }
    public FileWorkflow Workflow { get; }
    public RecoveryCoordinator Recovery { get; }

    /// <summary>복구 타이머를 멈추고 PDFium 스레드를 끝낸다(이 순서). 두 번 불러도 한 번만 한다.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Recovery.Dispose();
        Pdfium.Dispose();
    }
}
