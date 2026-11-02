using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using PdfEditor.App.Shell;
using PdfEditor.App.Tests.Viewer.Annotations;
using Xunit;

namespace PdfEditor.App.Tests.Shell;

public class ZoomSliderTests
{
    private static Window Show(MainWindowViewModel vm)
    {
        var app = Application.Current!;
        if (!app.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>().Any()) app.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        var w = new Window { Width = 800, Height = 30, Content = new StatusBar { DataContext = vm } };
        w.Show();
        w.UpdateLayout();
        return w;
    }

    [Fact]
    public void Zoom_slider_uses_the_round_thumb_and_thin_track()
    {
        HeadlessUi.Run(() =>
        {
            var w = Show(new MainWindowViewModel());
            var slider = w.GetVisualDescendants().OfType<Slider>().Single();
            Assert.Contains("zoom", slider.Classes);
            var thumb = slider.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().Single();
            var ellipse = thumb.GetVisualDescendants().OfType<Ellipse>().Single();
            Assert.Equal(14, thumb.Bounds.Width, 0.5);
            Assert.Equal(14, thumb.Bounds.Height, 0.5);
            Assert.NotNull(ellipse);
            // 트랙은 가는 막대(3 DIP)만, 반달 모양의 큰 막대는 없다.
            Assert.All(slider.GetVisualDescendants().OfType<Border>().Where(b => b.Height > 0 && !double.IsNaN(b.Height)), b => Assert.True(b.Height <= 3.01));
            var path = Environment.GetEnvironmentVariable("SEDOL_WRITE_SLIDER_PREVIEW");
            if (!string.IsNullOrEmpty(path))
            {
                var frame = w.CaptureRenderedFrame();
#pragma warning disable CS0618
                frame!.Save(path);
#pragma warning restore CS0618
            }
            w.Close();
            return 0;
        });
    }
}
