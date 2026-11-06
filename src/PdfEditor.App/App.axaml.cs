using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace PdfEditor.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 창이 여럿일 수 있다: 어느 창이든 마지막 창이 닫히면 앱이 끝난다(첫 창이 특별하지 않음).
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            var host = new Hosting.AppHost(AppContext.BaseDirectory);
            // 윈도우 종료·로그오프: 창이 여럿이면 창마다 동시에 묻지 않도록 막고 끝내기와 같은 순서로 하나씩 닫는다.
            // 어느 창에서든 취소하면 종료가 막힌다(창 하나일 때와 같음). 창이 하나면 지금처럼 그 창의 닫기 확인을 탄다.
            desktop.ShutdownRequested += (_, e) =>
            {
                if (host.Registry.Count <= 1) return;
                e.Cancel = true;
                _ = host.ExitAllAsync();
            };
            var window = new Shell.MainWindow(host);
            desktop.MainWindow = window;
            IReadOnlyList<string> files = Program.Bench is not null ? [] : (desktop.Args ?? []).Where(x => !x.StartsWith("-") && File.Exists(x)).ToList();
            // 명령줄 인자로 받은 파일·복구 질문은 첫 창이 뜬 뒤에.
            window.Opened += async (_, _) => await host.StartAsync(window, files);
            Program.Instance?.StartServer(paths => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                // 보내는 쪽이 전체 경로로 바꿔 보낸다. 상대 경로는 이 프로세스의 현재 폴더 기준이 되어 다른 파일을 열 수 있으므로 버린다.
                var list = paths.Where(p => p.Length < 32768 && Path.IsPathFullyQualified(p) && File.Exists(p)).ToList();
                await host.OpenExternalAsync(list);
            }));
            if (Program.Bench is { } bench)
                window.Opened += async (_, _) => await Bench.BenchRunner.RunAsync(window, bench);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
