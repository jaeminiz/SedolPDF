using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using PdfEditor.App.Dialogs;
using PdfEditor.App.Shell;
using PdfEditor.App.Tests.Viewer.Annotations;
using Xunit.Abstractions;

namespace PdfEditor.App.Tests.I18n;

/// <summary>
/// 영어 화면 맞춤 점검 (0.4.0 Task 10): 가장 좁은 창(최소 폭)에서 리본 탭·대화상자를 그려 글자가 잘리거나 창 밖으로 나가는 곳을 찾는다.
/// 환경 변수 SEDOL_LAYOUT_DIR가 있으면 그 폴더에 PNG를 저장해 눈으로도 볼 수 있다.
/// </summary>
public class EnglishLayoutTests(ITestOutputHelper output)
{
    private static string? OutDir => Environment.GetEnvironmentVariable("SEDOL_LAYOUT_DIR");

    /// <summary>글자 상자의 실제 너비가 글자 자체의 너비보다 좁으면 잘린 것(줄바꿈이 있는 상자는 높이로 판단).</summary>
    internal static List<string> Problems(Window w, string where)
    {
        var list = new List<string>();
        foreach (var tb in w.GetVisualDescendants().OfType<TextBlock>())
        {
            if (!tb.IsEffectivelyVisible || string.IsNullOrEmpty(tb.Text) || tb.Text.Length <= 1 || tb.Bounds.Width <= 0) continue;   // 한 글자 기호(↕ 등)는 글꼴 대체 때문에 폭을 정확히 재지 못한다
            var ft = new FormattedText(tb.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight), tb.FontSize, Brushes.Black);
            bool clipped;
            if (tb.TextWrapping == TextWrapping.NoWrap) clipped = ft.Width > tb.Bounds.Width + 1.0;
            else
            {
                // 줄바꿈 상자: 낱말 하나가 상자보다 넓거나 높이가 모자라면 잘린 것
                clipped = tb.Bounds.Height > 0 && tb.TextLayout is { } tl && tl.Height > tb.Bounds.Height + 1.0;
            }
            var origin = tb.TranslatePoint(default, w);
            // 리본은 가로로 스크롤되므로(두 언어 모두) 스크롤 영역 안의 글자는 창 밖에 있어도 잘못이 아니다.
            bool scrolls = tb.GetVisualAncestors().OfType<ScrollViewer>().Any();
            bool outside = !scrolls && origin is { } o && (o.X + tb.Bounds.Width > w.Bounds.Width + 1.0 || o.X < -1.0);
            if (clipped || outside)
                list.Add($"{where}: '{tb.Text}' ({(clipped ? "잘림" : "창 밖")}, 상자 {tb.Bounds.Width:0}px, 글자 {ft.Width:0}px)");
        }
        return list;
    }

    internal static void Save(Window w, string name)
    {
        if (OutDir is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        if (w.CaptureRenderedFrame() is { } bmp) { using var fs = File.Create(Path.Combine(dir, name + ".png")); bmp.Save(fs, new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
    }

    internal static void Pump(Window w)
    {
        for (int i = 0; i < 4; i++)
        {
            w.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>헤드리스 앱에는 App.axaml의 테마·리본 스타일·아이콘이 없다. 이 시험 동안만 같은 것을 붙였다가 뗀다.</summary>
    internal sealed class AppStyles : IDisposable
    {
        private readonly Avalonia.Styling.IStyle[] _styles;
        private readonly Avalonia.Controls.IResourceProvider _icons;

        public AppStyles()
        {
            var app = Application.Current!;
            var baseUri = new Uri("avares://SedolPDF/");
            _styles =
            [
                new Avalonia.Themes.Fluent.FluentTheme(),
                new Avalonia.Markup.Xaml.Styling.StyleInclude(baseUri) { Source = new Uri("avares://SedolPDF/Shell/Ribbon/RibbonStyles.axaml") },
            ];
            _icons = new Avalonia.Markup.Xaml.Styling.ResourceInclude(baseUri) { Source = new Uri("avares://SedolPDF/Resources/Icons.axaml") };
            foreach (var s in _styles) app.Styles.Add(s);
            app.Resources.MergedDictionaries.Add(_icons);
        }

        public void Dispose()
        {
            var app = Application.Current!;
            foreach (var s in _styles) app.Styles.Remove(s);
            app.Resources.MergedDictionaries.Remove(_icons);
        }
    }

    [Fact]
    public void English_RibbonTabs_FitAtMinimumWidth()
    {
        var problems = HeadlessUi.Run(() =>
        {
            using var styles = new AppStyles();
            using var _ = new CultureScope("en");
            var w = new MainWindow { Width = 900, Height = 600 };
            w.Show();
            Pump(w);
            var vm = (MainWindowViewModel)w.DataContext!;
            var all = new List<string>();
            foreach (var tab in vm.RibbonTabs)
            {
                vm.SelectRibbonTabCommand.Execute(tab.Id);
                Pump(w);
                Save(w, "en-ribbon-" + tab.Id);
                all.AddRange(Problems(w, "탭 " + tab.Id));
            }
            w.Close();
            return all;
        });
        foreach (var p in problems) output.WriteLine(p);
        Assert.Empty(problems);
    }

    [Fact]
    public void Korean_RibbonTabs_StillFitAtMinimumWidth()
    {
        var problems = HeadlessUi.Run(() =>
        {
            using var styles = new AppStyles();
            using var _ = new CultureScope("ko");
            var w = new MainWindow { Width = 900, Height = 600 };
            w.Show();
            Pump(w);
            var vm = (MainWindowViewModel)w.DataContext!;
            var all = new List<string>();
            foreach (var tab in vm.RibbonTabs)
            {
                vm.SelectRibbonTabCommand.Execute(tab.Id);
                Pump(w);
                Save(w, "ko-ribbon-" + tab.Id);
                all.AddRange(Problems(w, "탭 " + tab.Id));
            }
            w.Close();
            return all;
        });
        foreach (var p in problems) output.WriteLine(p);
        Assert.Empty(problems);
    }

    private static Dictionary<string, double> RibbonExtents(string lang, double width) => HeadlessUi.Run(() =>
    {
        using var styles = new AppStyles();
        using var _ = new CultureScope(lang);
        var w = new MainWindow { Width = width, Height = 600 };
        w.Show();
        Pump(w);
        var vm = (MainWindowViewModel)w.DataContext!;
        var d = new Dictionary<string, double>();
        foreach (var tab in vm.RibbonTabs)
        {
            vm.SelectRibbonTabCommand.Execute(tab.Id);
            Pump(w);
            var sv = w.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.Extent.Width > 0 && s.Bounds.Height < 140 && s.Bounds.Height > 60).FirstOrDefault();
            d[tab.Id] = sv is null ? 0 : sv.Extent.Width;
        }
        w.Close();
        return d;
    });

    [Fact]
    public void English_Ribbon_IsNotMuchWiderThanKorean()
    {
        // 영어가 한국어보다 리본이 크게 넓어지면 좁은 창에서 스크롤이 훨씬 길어진다(스크롤은 두 언어 모두 있다).
        foreach (double width in new[] { 900.0, 1280.0 })
        {
            var ko = RibbonExtents("ko", width); var en = RibbonExtents("en", width);
            foreach (var (tab, kw) in ko)
            {
                output.WriteLine($"{width}px {tab}: ko {kw:0} / en {en[tab]:0}");
                Assert.True(en[tab] <= kw * 1.25 + 10, $"{tab}@{width}: ko {kw:0} / en {en[tab]:0}");
            }
        }
    }

    [Fact]
    public void English_Dialogs_FitTheirWindows()
    {
        var problems = HeadlessUi.Run(() =>
        {
            using var _ = new CultureScope("en");
            var all = new List<string>();
            void Check(string name, Window d)
            {
                d.Show(); Pump(d);
                Save(d, "en-dialog-" + name);
                all.AddRange(Problems(d, name));
                d.Close();
            }
            Check("about", new AboutDialog());
            Check("settings", new SettingsDialog(5, null, "en"));
            Check("help", new HelpDialog());
            Check("insert", new InsertPagesDialog("long-drawing-file-name.pdf", 12));
            Check("move", new MovePagesDialog(40, 3));
            Check("merge", new MergeDialog(["a.pdf", "b.pdf"]));
            Check("password", new PasswordDialog("long-drawing-file-name.pdf", false));
            Check("message", new MessageDialog(PdfEditor.App.Resources.Strings.Dlg_UnsavedTitle, string.Format(PdfEditor.App.Resources.Strings.Dlg_UnsavedMessage, "drawing.pdf"),
                [PdfEditor.App.Resources.Strings.Dlg_Save, PdfEditor.App.Resources.Strings.Dlg_Discard, PdfEditor.App.Resources.Strings.Dlg_Cancel]));
            return all;
        });
        foreach (var p in problems) output.WriteLine(p);
        Assert.Empty(problems);
    }
}
