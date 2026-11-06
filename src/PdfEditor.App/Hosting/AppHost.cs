using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Pdf.Native;
using PdfEditor.App.Dialogs;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Settings;
using PdfEditor.App.Shell;
using PdfEditor.Document.Recovery;
using PdfEditor.Document.Saving;

namespace PdfEditor.App.Hosting;

/// <summary>
/// 앱에 하나인 공유 서비스(0.8.0 여러 창 설계 4.1). PDFium 스레드·설정·열린 세션 목록(FileWorkflow)·복구·창 목록을 갖는다.
/// 세션은 PDFium 스레드에 묶여 있으므로 이 스레드를 앱 하나로 두어야 세션을 창 사이로 옮길 수 있다.
/// 창마다 다른 것(인쇄·용량 줄이기 대화상자, 패널 상태)은 창이 만든다.
/// </summary>
public sealed class AppHost : IDisposable
{
    private bool _disposed;

    /// <param name="prompts">시험용 대화상자 대역. null이면 실제 대화상자.</param>
    public AppHost(string baseDirectory, IUserPrompts? prompts = null)
    {
        // 앱 공통 대화상자(저장 확인·암호·복구 등)는 마지막으로 활성화된, 아직 닫히지 않은 창 위에 띄운다.
        Func<Window> owner = () => (Window)Registry.LastActive!;
        Prompts = prompts ?? new AvaloniaPrompts(owner);
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
            new AvaloniaRecoveryUi(owner), Prompts);
        Recovery.Attach(Workflow);
        Transfer = new TabTransfer(this);
    }

    public PdfiumThread Pdfium { get; } = new();   // 앱 전체에서 PdfiumThread는 하나
    public AppearanceServices Services { get; }
    public AppPaths Paths { get; }
    public SettingsStore Settings { get; }
    public IUserPrompts Prompts { get; }
    public FileWorkflow Workflow { get; }
    public RecoveryCoordinator Recovery { get; }
    public WindowRegistry Registry { get; } = new();
    public TabTransfer Transfer { get; }

    /// <summary>
    /// 새 창을 만들어 띄운다. copyFrom이 있으면 그 창의 패널 배치로 시작하고, 크기는 같게(최대화면 기본 크기),
    /// 위치는 at(화면 물리 픽셀) 또는 원래 창에서 조금 비켜서.
    /// </summary>
    public MainWindow CreateWindow(MainWindow? copyFrom, PixelPoint? at = null)
    {
        var w = new MainWindow(this);
        if (copyFrom is not null)
        {
            w.ViewModel.CopyWindowStateFrom(copyFrom.ViewModel);
            if (copyFrom.WindowState == WindowState.Normal) { w.Width = copyFrom.Width; w.Height = copyFrom.Height; }
        }
        var pos = at ?? (copyFrom is null ? null : new PixelPoint(copyFrom.Position.X + 32, copyFrom.Position.Y + 32));
        if (pos is { } p)
        {
            // 화면 밖(작업 표시줄 아래, 모니터 바깥)에 뜨지 않게 놓을 모니터의 작업 영역 안으로 옮기고, 더 크면 줄인다.
            var screens = copyFrom?.Screens;
            if (screens is not null && screens.All.Count > 0 && !double.IsNaN(w.Width) && !double.IsNaN(w.Height))
            {
                double s = screens.ScreenFromPoint(p)?.Scaling ?? copyFrom!.RenderScaling;
                var wanted = new PixelRect(p, PixelSize.FromSize(new Size(w.Width, w.Height), s));
                var fit = WindowPlacement.FitToWorkArea(wanted, [.. screens.All.Select(x => x.WorkingArea)]);
                p = fit.Position;
                if (fit.Size != wanted.Size) { w.Width = fit.Width / s; w.Height = fit.Height / s; }
            }
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Position = p;
        }
        w.Show();
        return w;
    }

    /// <summary>첫 창이 뜬 뒤 한 번: 이식 폴더 안내 → 남은 복구 항목 묻기(모두 첫 창에) → 복구 타이머 시작 → 명령줄 파일 열기.</summary>
    public async Task StartAsync(MainWindow first, IReadOnlyList<string> startupFiles)
    {
        if (Paths.FellBackFromPortable)
        {
            try { await Prompts.ShowInfoAsync(Strings.Msg_PortableFolderReadOnly); }
            catch (Exception) { /* 안내 실패가 시작을 막지 않는다 */ }
        }
        // 먼저 이전에 남은 복구 항목을 묻고, 그다음 명령줄 파일을 연다(같은 파일이면 복구한 탭으로 전환됨).
        try
        {
            foreach (var s in await Recovery.RecoverAtStartupAsync()) first.ViewModel.ShowSession(s);
        }
        catch (Exception) { /* 복구 실패가 시작을 막지 않는다 */ }
        Recovery.Start(() => Dispatcher.UIThread.InvokeAsync(() => Workflow.OpenSessions).GetTask());
        if (startupFiles.Count > 0) await first.ViewModel.OpenManyAsync(startupFiles);
    }

    /// <summary>단일 인스턴스로 들어온 파일: 마지막 활성 창을 앞으로 가져와 거기에 연다.</summary>
    public async Task OpenExternalAsync(IReadOnlyList<string> paths)
    {
        if (Registry.LastActive is not MainWindow w) return;
        w.BringToFront();
        await w.ViewModel.OpenManyAsync(paths);
        w.Activate();
    }

    /// <summary>파일 > 끝내기: 창을 최근 순서로 하나씩 닫는다. 어느 창에서든 취소하면 거기서 멈추고 남은 창은 그대로.</summary>
    public async Task ExitAllAsync()
    {
        foreach (var w in Registry.ByRecent.OfType<MainWindow>().ToList())
            if (!await w.TryCloseAsync()) return;
    }

    public bool IsDisposed => _disposed;

    /// <summary>복구 타이머를 멈추고 PDFium 스레드를 끝낸다(이 순서). 두 번 불러도 한 번만 한다.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Recovery.Dispose();
        Pdfium.Dispose();
    }
}
