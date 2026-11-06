using System.IO;
using Avalonia;
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
            Shell.MainWindow? first = null;
            var host = new Hosting.AppHost(AppContext.BaseDirectory, () => first!);
            var window = first = new Shell.MainWindow(host,
                Program.Bench is not null ? [] : (desktop.Args ?? []).Where(x => !x.StartsWith("-") && File.Exists(x)).ToList());
            desktop.MainWindow = window;
            Program.Instance?.StartServer(paths => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                // 보내는 쪽이 전체 경로로 바꿔 보낸다. 상대 경로는 이 프로세스의 현재 폴더 기준이 되어 다른 파일을 열 수 있으므로 버린다.
                var files = paths.Where(p => p.Length < 32768 && Path.IsPathFullyQualified(p) && File.Exists(p)).ToList();
                if (window.WindowState == Avalonia.Controls.WindowState.Minimized) window.WindowState = Avalonia.Controls.WindowState.Normal;
                window.Activate();
                await window.ViewModel.OpenManyAsync(files);
                window.Activate();
            }));
            if (Program.Bench is { } bench)
                window.Opened += async (_, _) => await Bench.BenchRunner.RunAsync(window, bench);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
