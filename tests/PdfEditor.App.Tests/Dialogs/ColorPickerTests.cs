using Avalonia.Controls;
using PdfEditor.App.Dialogs;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.Document.Annotations;
using Xunit;

namespace PdfEditor.App.Tests.Dialogs;

/// <summary>사용자 색 고르기(2026-10-22 사용자 요청).</summary>
public class ColorPickerTests
{
    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0, 0, 255)]
    [InlineData(255, 255, 255)]
    [InlineData(0, 0, 0)]
    [InlineData(128, 64, 200)]
    [InlineData(229, 55, 45)]
    public void Hsv_RoundTrips_AllRgb(byte r, byte g, byte b)
    {
        var c = new PdfColor(r, g, b);
        var (h, s, v) = ColorMath.ToHsv(c);
        var back = ColorMath.FromHsv(h, s, v);
        Assert.InRange(back.R - c.R, -1, 1);
        Assert.InRange(back.G - c.G, -1, 1);
        Assert.InRange(back.B - c.B, -1, 1);
    }

    [Theory]
    [InlineData("#E5372D", 229, 55, 45)]
    [InlineData("e5372d", 229, 55, 45)]
    [InlineData("  #0000FF ", 0, 0, 255)]
    public void TryParseHex_Accepts(string text, byte r, byte g, byte b) => Assert.Equal(new PdfColor(r, g, b), ColorMath.TryParseHex(text));

    [Theory]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("#GGGGGG")]
    [InlineData(null)]
    public void TryParseHex_RejectsWrongText(string? text) => Assert.Null(ColorMath.TryParseHex(text));

    [Fact]
    public void Dialog_StartsAtGivenColor_AndFollowsHexInput()
    {
        HeadlessUi.Run(() =>
        {
            var d = new ColorPickerDialog(new PdfColor(10, 120, 200));
            var c0 = d.Current();
            Assert.InRange(c0.R - 10, -1, 1); Assert.InRange(c0.G - 120, -1, 1); Assert.InRange(c0.B - 200, -1, 1);
            d.Show();
            var hex = d.GetVisualDescendantsOfType<TextBox>().First(t => t.MaxLength == 7);
            hex.Text = "#FF8000";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(new PdfColor(255, 128, 0), d.Current());
            d.Close();
            return 0;
        });
    }
}

internal static class VisualExt
{
    public static IEnumerable<T> GetVisualDescendantsOfType<T>(this Avalonia.Visual v) where T : class
        => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(v).OfType<T>();
}
