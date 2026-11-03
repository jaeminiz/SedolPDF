using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.VisualTree;
using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Printing;
using PdfEditor.App.Styles;
using PdfEditor.App.Tests.I18n;
using PdfEditor.App.Tests.Panels;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.TestFixtures;
using Xunit.Abstractions;

namespace PdfEditor.App.Tests.Shell;

/// <summary>
/// 디자인 규격표(docs/design/design-tokens.md) 시험: 코드 상수와 리소스 값이 같은지, 단추·체크 상자·라디오의 글자가 세로 가운데인지,
/// 컨트롤 높이가 규격표 값인지, 토큰 밖 FontSize 하드코딩이 새로 생기지 않는지.
/// 환경 변수 SEDOL_TOKEN_SHOTS_DIR가 있으면 그 폴더에 tokens-after-*.png를 저장한다.
/// </summary>
public class DesignTokenTests(ITestOutputHelper output) : PanelTestBase
{
    private static string? ShotDir => Environment.GetEnvironmentVariable("SEDOL_TOKEN_SHOTS_DIR");

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "PdfEditor.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("저장소 루트를 찾지 못함");
    }

    private sealed class FakeCatalog : IPrinterCatalog
    {
        public IReadOnlyList<string> ListPrinters() => ["Fake A", "Fake B"];
        public string? DefaultPrinter() => "Fake B";
        public PrinterCaps QueryCaps(string printer) => new([new PaperInfo(9, "A4", 2100, 2970), new PaperInfo(1, "Letter", 2159, 2794)], 1, true, true, true);
        public byte[]? DefaultDevMode(string printer) => [1, 2, 3];
        public byte[] Apply(string printer, byte[] devMode, PrintOptions o, bool landscape, bool driverCopies, PrinterCaps caps) => [9, 9];
        public PrintSurface? Measure(string printer, byte[]? devMode) => new(4960, 7016, 600, 600, 4961, 7016, 0, 0);
        public byte[]? ShowDriverDialog(IntPtr owner, string printer, byte[] devMode) => null;
    }

    [Fact]
    public void CodeConstants_MatchResources()
    {
        HeadlessUi.Run(() =>
        {
            using var _ = new EnglishLayoutTests.AppStyles();
            double R(string k) => Assert.IsType<double>(Application.Current!.FindResource(k));
            Assert.Equal(DesignTokens.FontCaption, R("FontSizeCaption"));
            Assert.Equal(DesignTokens.FontBody, R("FontSizeBody"));
            Assert.Equal(DesignTokens.FontSubtitle, R("FontSizeSubtitle"));
            Assert.Equal(DesignTokens.FontTitle, R("FontSizeTitle"));
            Assert.Equal(DesignTokens.HeightS, R("ControlHeightS"));
            Assert.Equal(DesignTokens.HeightM, R("ControlHeightM"));
            Assert.Equal(DesignTokens.HeightL, R("ControlHeightL"));
            Assert.Equal(DesignTokens.IconS, R("IconSizeS"));
            Assert.Equal(DesignTokens.IconM, R("IconSizeM"));
            Assert.Equal(DesignTokens.IconL, R("IconSizeL"));
            return 0;
        });
    }

    [Fact]
    public void CommonControls_HaveTokenHeights_AndCenteredContent()
    {
        var problems = HeadlessUi.Run(() =>
        {
            using var styles = new EnglishLayoutTests.AppStyles();
            var small = new Button { Content = "Small", Classes = { "small" } };
            var panel = new StackPanel
            {
                Spacing = 4, Width = 240,
                Children =
                {
                    new Button { Content = "Button" }, small, new Button { Content = "Large", Classes = { "large" } },
                    new ToggleButton { Content = "Toggle" }, new TextBox { Text = "Text" }, new ComboBox { ItemsSource = new[] { "A", "B" }, SelectedIndex = 0 },
                    new CheckBox { Content = "Check" }, new RadioButton { Content = "Radio" },
                },
            };
            var w = new Window { Width = 300, Height = 400, Content = panel };
            w.Show();
            EnglishLayoutTests.Pump(w);
            var list = new List<string>();
            void Height(Control c, double expect)
            {
                if (Math.Abs(c.Bounds.Height - expect) > 0.6) list.Add($"{c.GetType().Name} 높이 {c.Bounds.Height} ≠ {expect}");
            }
            Height((Control)panel.Children[0], DesignTokens.HeightM); Height(small, DesignTokens.HeightS); Height((Control)panel.Children[2], DesignTokens.HeightL);
            Height((Control)panel.Children[3], DesignTokens.HeightM); Height((Control)panel.Children[4], DesignTokens.HeightM); Height((Control)panel.Children[5], DesignTokens.HeightM);
            Height((Control)panel.Children[6], DesignTokens.HeightS); Height((Control)panel.Children[7], DesignTokens.HeightS);
            list.AddRange(CenterProblems(w, "공통"));
            foreach (var n in new[] { "Box", "Ring" })
                foreach (var e in w.GetVisualDescendants().OfType<Control>().Where(c => c.Name == n))
                    if (Math.Abs(e.Bounds.Width - DesignTokens.IconM) > 0.6) list.Add($"{n} 크기 {e.Bounds.Width} ≠ 16");
            w.Close();
            return list;
        });
        foreach (var p in problems) output.WriteLine(p);
        Assert.Empty(problems);
    }

    /// <summary>단추 안 글자·체크 상자·라디오 옆 글자의 세로 중심이 컨트롤 중심과 1.5 DIP 안에 있어야 한다.</summary>
    internal static List<string> CenterProblems(Window w, string where)
    {
        var list = new List<string>();
        foreach (var c in w.GetVisualDescendants().OfType<Control>().Where(c => c is Button or ToggleButton or CheckBox or RadioButton && c.IsEffectivelyVisible))
        {
            if (c.GetType().Namespace!.Contains("Ribbon")) continue;
            Control? tb = c.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => !string.IsNullOrEmpty(t.Text) && t.Bounds.Height > 0);
            tb ??= c.GetVisualDescendants().OfType<AccessText>().FirstOrDefault(t => !string.IsNullOrEmpty(t.Text) && t.Bounds.Height > 0);
            if (tb is null || c.Bounds.Height <= 0) continue;
            var top = tb.TranslatePoint(default, c);
            if (top is null) continue;
            double textCenter = top.Value.Y + tb.Bounds.Height / 2, ctrlCenter = c.Bounds.Height / 2;
            if (Math.Abs(textCenter - ctrlCenter) > 1.5)
                list.Add($"{where}: {c.GetType().Name} 글자 중심 {textCenter:0.0} ≠ 컨트롤 중심 {ctrlCenter:0.0}");
        }
        return list;
    }

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public async Task Panels_AndPrintDialog_RenderCenteredWithoutClipping(string lang)
    {
        HeadlessUi.Run(() => 0);
        string p = Path.Combine(Dir, "tok.pdf");
        TestPdfFactory.CreateLabeled(p, [(400, 600)]);
        var tab = await OpenTab(p);
        var problems = HeadlessUi.Run(() =>
        {
            using var styles = new EnglishLayoutTests.AppStyles();
            using var _ = new CultureScope(lang);
            var all = new List<string>();
            void One(string name, Control content, double width, double height = 700)
            {
                var w = new Window { Width = width + 20, Height = height, Content = new Border { Width = width, Child = content } };
                w.Show();
                EnglishLayoutTests.Pump(w);
                Shot(w, $"{lang}-{name}");
                all.AddRange(CenterProblems(w, $"{lang} {name}"));
                all.AddRange(EnglishLayoutTests.Problems(w, $"{lang} {name}"));
                w.Close();
            }
            One("findreplace", new FindReplacePanel { DataContext = new FindReplaceViewModel(tab) { IsScopeRange = true, FindText = "H-2501", ReplaceText = "H-2502" } }, 340);
            using var comments = new CommentsViewModel(tab);
            One("comments", new CommentsPanel { DataContext = comments }, 320);
            One("search", new SearchPanel { DataContext = new SearchViewModel(tab) }, 200);
            One("properties", new PropertiesPanel { DataContext = new PropertiesViewModel(tab) }, 220);
            var dlg = new PrintDialog(new PrintDialogContext(Enumerable.Range(0, 5).Select(_ => (595f, 842f)).ToList(), 0, [], new FakeCatalog(), new PrintOptions(), null));
            dlg.Show();
            EnglishLayoutTests.Pump(dlg);
            Shot(dlg, $"{lang}-print");
            all.AddRange(CenterProblems(dlg, $"{lang} print"));
            all.AddRange(EnglishLayoutTests.Problems(dlg, $"{lang} print"));
            dlg.Close();
            return all;
        });
        foreach (var pr in problems) output.WriteLine(pr);
        Assert.Empty(problems);
    }

    private static void Shot(Window w, string name)
    {
        if (ShotDir is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        if (w.CaptureRenderedFrame() is { } bmp)
        {
            using var fs = File.Create(Path.Combine(dir, $"tokens-after-{name}.png"));
            bmp.Save(fs, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }

    /// <summary>토큰 밖 글자 크기 숫자를 새로 박지 못하게 한다. 허용 목록은 이유를 적은 곳만.</summary>
    [Fact]
    public void NoHardCodedFontSizes_OutsideAllowList()
    {
        var root = Path.Combine(RepoRoot(), "src", "PdfEditor.App");
        var allow = new (string File, string Value)[]
        {
            ("Dialogs/AboutDialog.axaml", "26"),   // 정보 창의 제품 이름 표제(규격 밖 크기 하나뿐)
        };
        var axaml = new Regex("(?:\\bFontSize=\"|Property=\"FontSize\"\\s+Value=\")(\\d[\\d.]*)\"");
        var code = new Regex(@"\bFontSize\s*=\s*(\d[\d.]*)\b");
        var sep = Path.DirectorySeparatorChar;
        var bad = new List<string>();
        foreach (var f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".axaml") || f.EndsWith(".cs")) && !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}")
                                 && !f.Contains($"{sep}Bench{sep}") && !f.EndsWith("DesignTokens.cs")))
        {
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            var text = File.ReadAllText(f);
            foreach (Match m in (f.EndsWith(".axaml") ? axaml : code).Matches(text))
                if (!allow.Any(a => a.File == rel && a.Value == m.Groups[1].Value))
                    bad.Add($"{rel}: FontSize {m.Groups[1].Value}");
        }
        foreach (var b in bad) output.WriteLine(b);
        Assert.Empty(bad);
    }
}
