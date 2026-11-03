using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Pdf.Native;
using PdfEditor.App.Dialogs;
using PdfEditor.App.Printing;
using PdfEditor.App.Shell;
using PdfEditor.App.Tests.I18n;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Saving;
using Xunit.Abstractions;

namespace PdfEditor.App.Tests.Shell;

/// <summary>
/// 스토어·README용 스크린샷 생성기. 환경 변수 SEDOL_STORE_SHOTS_DIR(저장 폴더)와 SEDOL_STORE_SAMPLE(샘플 PDF)이 둘 다 있을 때만 일하고,
/// 없으면 아무것도 하지 않고 통과한다(일반 시험 실행에 영향 없음). 한국어는 폴더 바로 아래, 영어는 en 하위 폴더에 저장한다.
/// 샘플 PDF에는 먼저 주석(형광펜·텍스트 상자·설명선·스티커)을 넣은 사본을 만들어 쓴다.
/// </summary>
public class StoreScreenshotTests(ITestOutputHelper output)
{
    private static string? Dir => Environment.GetEnvironmentVariable("SEDOL_STORE_SHOTS_DIR");
    private static string? Sample => Environment.GetEnvironmentVariable("SEDOL_STORE_SAMPLE");

    private const double W = 1600, H = 1000;
    private const float PageH = 841.89f;

    // 0.8배 화면 그림에서 잰 위쪽 기준 픽셀 좌표(px)를 쪽 좌표(pt)로.
    private static PdfRectF R(double x0, double y0, double x1, double y1)
        => new((float)(x0 / 0.8), PageH - (float)(y1 / 0.8), (float)(x1 / 0.8), PageH - (float)(y0 / 0.8));

    private static PdfPointF P(double x, double y) => new((float)(x / 0.8), PageH - (float)(y / 0.8));

    private static readonly PdfColor Yellow = new(255, 235, 0), Red = new(214, 40, 40), Blue = new(0, 95, 184), Ink = new(30, 30, 30);

    private static Annotation Make(DocumentSession s, AnnotKind kind, PdfRectF rect, Func<Annotation, Annotation> f)
        => f(new Annotation
        {
            Id = s.Annotations.NewId(), Kind = kind, Rect = rect, Name = Guid.NewGuid().ToString("D"),
            Flags = PdfAnnotFlags.Print, Author = "SedolPDF", CreatedAt = DateTimeOffset.Now,
        });

    /// <summary>샘플 PDF 사본에 주석을 넣어 저장한다.</summary>
    private static async Task<string> BakeAnnotationsAsync(string src, string target)
    {
        using var t = new PdfiumThread();
        var services = AppearanceServices.Create(AppContext.BaseDirectory);
        await using var s = await DocumentSession.OpenAsync(t, src, null);
        await s.AnnotationsReady;
        async Task Add(int page, Annotation a) => await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[page].Id, a));

        // 1쪽: 형광펜, 텍스트 상자, 설명선
        var hl = R(42, 117, 436, 132);
        await Add(0, Make(s, AnnotKind.Highlight, hl, a => a with { Color = Yellow, Opacity = 0.5f, Quads = [PdfQuadF.FromRect(hl)] }));
        var tb = R(318, 20, 436, 44);
        await Add(0, Make(s, AnnotKind.FreeText, tb, a => a with
        {
            Intent = FreeTextIntent.TextBox, Contents = "검토 중 · 초안 v2", FontSizePt = 11, TextColor = Red, Color = Red, FillColor = new PdfColor(255, 247, 214), BorderWidth = 1.2f,
        }));
        var box = R(262, 268, 436, 306);
        var tip = P(402, 218);
        var leader = ImmutableArray.Create(tip, P(402, 268));
        await Add(0, Make(s, AnnotKind.FreeText, box, a =>
        {
            var c = a with
            {
                Intent = FreeTextIntent.Callout, Contents = "실시간 시세 영역을\n실제 화면으로 교체", FontSizePt = 11, TextColor = Ink, Color = Blue,
                FillColor = new PdfColor(232, 241, 252), BorderWidth = 1.4f, TextBox = box, Vertices = leader, EndEnding = LineEnding.OpenArrow,
            };
            return c with { Rect = AnnotationGeometry.ComputeRect(c) };
        }));
        // 2쪽: 형광펜과 밑줄, 스티커 메모
        var hl2 = R(358, 330, 436, 343);
        await Add(1, Make(s, AnnotKind.Highlight, hl2, a => a with { Color = Yellow, Opacity = 0.5f, Quads = [PdfQuadF.FromRect(hl2)] }));
        var ul = R(42, 392, 150, 405);
        await Add(1, Make(s, AnnotKind.Underline, ul, a => a with { Color = Red, Quads = [PdfQuadF.FromRect(ul)] }));
        await Add(1, Make(s, AnnotKind.Text, R(448, 312, 462, 326), a => a with { Color = Yellow, Contents = "RSI 기준 값 확인 요청" }));
        // 3쪽: 사각형 표시
        await Add(2, Make(s, AnnotKind.Square, R(42, 276, 436, 346), a => a with { Color = Red, BorderWidth = 2f }));
        await new SaveService([], services.Appearance, services.Patcher, services.Assets).SaveAsync(s, target);
        return target;
    }

    private static void Wait(Window w, Func<bool> done, int maxMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            EnglishLayoutTests.Pump(w);
            if (done()) break;
            Thread.Sleep(30);
        }
        for (int i = 0; i < 25; i++) { EnglishLayoutTests.Pump(w); Thread.Sleep(30); }   // 타일 그림이 화면에 올라오도록
    }

    private static Bitmap Frame(Window w) => w.CaptureRenderedFrame() ?? throw new InvalidOperationException("캡처 실패");

    private static void Save(Bitmap b, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        b.Save(fs, new PngBitmapEncoderOptions());
    }

    private sealed class FakeCatalog : IPrinterCatalog
    {
        public IReadOnlyList<string> ListPrinters() => ["Microsoft Print to PDF"];
        public string? DefaultPrinter() => "Microsoft Print to PDF";
        public PrinterCaps QueryCaps(string printer) => new([new PaperInfo(9, "A4", 2100, 2970), new PaperInfo(1, "Letter", 2159, 2794)], 1, true, true, true);
        public byte[]? DefaultDevMode(string printer) => [1, 2, 3];
        public byte[] Apply(string printer, byte[] devMode, PrintOptions o, bool landscape, bool driverCopies, PrinterCaps caps) => [9, 9];
        public PrintSurface? Measure(string printer, byte[]? devMode) => new(4960, 7016, 600, 600, 4961, 7016, 0, 0);
        public byte[]? ShowDriverDialog(IntPtr owner, string printer, byte[] devMode) => null;
    }

    [Fact]
    public async Task Generate_StoreScreenshots()
    {
        if (string.IsNullOrEmpty(Dir) || string.IsNullOrEmpty(Sample)) return;
        string work = Path.Combine(Path.GetTempPath(), "sedol-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string annotated = await BakeAnnotationsAsync(Sample, Path.Combine(work, "SEDOL Analytics PDF Product Showcase.pdf"));
            foreach (var lang in new[] { "ko", "en" })
            {
                string outDir = lang == "ko" ? Dir : Path.Combine(Dir, "en");
                var shots = HeadlessUi.Run(() => ShootMainWindow(annotated, lang));
                foreach (var (name, bmp) in shots) Save(bmp, Path.Combine(outDir, name + ".png"));
                var print = await ShootPrintAsync(annotated, lang);
                Save(print, Path.Combine(outDir, "06-print-preview.png"));
                output.WriteLine(lang + " done");
            }
        }
        finally { try { Directory.Delete(work, true); } catch (Exception) { } }
    }

    private static List<(string, Bitmap)> ShootMainWindow(string pdf, string lang)
    {
        using var styles = new EnglishLayoutTests.AppStyles();
        using var _ = new CultureScope(lang);
        var w = new MainWindow { Width = W, Height = H };
        w.Show();
        EnglishLayoutTests.Pump(w);
        var vm = (MainWindowViewModel)w.DataContext!;
        var open = vm.OpenAsync(pdf);
        Wait(w, () => open.IsCompleted, 30000);
        open.GetAwaiter().GetResult();
        vm.ActiveDocument!.Scale = 0.85;
        Wait(w, () => false);
        vm.ActiveDocument.Scale = 0.85;
        Wait(w, () => false);

        var list = new List<(string, Bitmap)>();
        void Tab(string id, string name)
        {
            vm.SelectRibbonTabCommand.Execute(id);
            Wait(w, () => false);
            list.Add((name, Frame(w)));
        }
        Tab("Home", "02-home");
        Tab("Annotate", "03-annotate");
        vm.ActiveDocument.CurrentPageIndex = 2; Wait(w, () => false);
        Tab("Edit", "04-edit");
        vm.ActiveDocument.CurrentPageIndex = 1; Wait(w, () => false);
        Tab("Page", "05-page");

        vm.ActiveDocument.CurrentPageIndex = 0; Wait(w, () => false);
        // 1) 정보: 홈 화면 위에 정보 창을 얹는다.
        vm.SelectRibbonTabCommand.Execute("Home");
        Wait(w, () => false);
        var main = Frame(w);
        var about = new AboutDialog();
        about.Show();
        EnglishLayoutTests.Pump(about);
        Wait(about, () => false, 500);
        var aboutFrame = Frame(about);
        about.Close();
        var overlay = new Window
        {
            Width = W, Height = H,
            Content = new Grid
            {
                Children =
                {
                    new Image { Source = main, Stretch = Stretch.Fill },
                    new Border { Background = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)) },
                    new Border
                    {
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        BoxShadow = BoxShadows.Parse("0 8 32 0 #80000000"),
                        Child = new Image { Source = aboutFrame, Width = aboutFrame.Size.Width, Height = aboutFrame.Size.Height },
                    },
                },
            },
        };
        overlay.Show();
        EnglishLayoutTests.Pump(overlay);
        list.Insert(0, ("01-info", Frame(overlay)));
        overlay.Close();
        w.Close();
        return list;
    }

    private static async Task<Bitmap> ShootPrintAsync(string pdf, string lang)
    {
        using var t = new PdfiumThread();
        var services = AppearanceServices.Create(AppContext.BaseDirectory);
        await using var session = await DocumentSession.OpenAsync(t, pdf, null);
        await session.AnnotationsReady;
        using var cts = new CancellationTokenSource();
        await using var source = await PrintSource.PrepareAsync(session, services.Appearance, services.Patcher, services.Assets, cts.Token);
        var sizes = session.Pages.Select(p => (p.WidthPt, p.HeightPt)).ToList();
        var ctx = new PrintDialogContext(sizes, 0, [], new FakeCatalog(), new PrintOptions(),
            async (sheet, o, surf, width, ct) => await session.Thread.InvokeAsync(() => (PreviewBitmap?)PrintPreviewRenderer.RenderSheet(source.Document, sheet, o, surf, width, ct), ct));
        return HeadlessUi.Run(() =>
        {
            using var styles = new EnglishLayoutTests.AppStyles();
            using var _ = new CultureScope(lang);
            var d = new PrintDialog(ctx) { Width = 980, Height = 880 };
            d.Show();
            EnglishLayoutTests.Pump(d);
            Wait(d, () => false, 6000);
            var f = Frame(d);
            d.Close();
            return f;
        });
    }
}
