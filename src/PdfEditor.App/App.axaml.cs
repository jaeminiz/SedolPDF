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
            var window = new Shell.MainWindow(
                Program.Bench is not null ? [] : (desktop.Args ?? []).Where(x => !x.StartsWith("-") && File.Exists(x)).ToList());
            desktop.MainWindow = window;
            if (Program.Bench is { } bench)
                window.Opened += async (_, _) => await Bench.BenchRunner.RunAsync(window, bench);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
