using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.App.Viewer;
using Xunit;

namespace PdfEditor.App.Tests.Viewer;

public class HandCursorTests
{
    private static (int Opaque, int Total, bool CornerClear) Stats(RenderTargetBitmap bmp)
    {
        int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height, stride = w * 4;
        var px = new byte[stride * h];
        var ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(px.Length);
        try
        {
            bmp.CopyPixels(new Avalonia.PixelRect(0, 0, w, h), ptr, px.Length, stride);
            System.Runtime.InteropServices.Marshal.Copy(ptr, px, 0, px.Length);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr); }
        int opaque = 0;
        for (int i = 3; i < px.Length; i += 4) if (px[i] > 200) opaque++;
        return (opaque, w * h, px[3] == 0);
    }

    [Theory]
    [InlineData(false, HandCursorStyle.Classic)]
    [InlineData(true, HandCursorStyle.Classic)]
    [InlineData(false, HandCursorStyle.Dark)]
    [InlineData(true, HandCursorStyle.Navy)]
    public void Cursor_bitmaps_are_32px_drawn_hands_with_transparent_corners(bool grab, HandCursorStyle style)
    {
        var (opaque, total, corner) = HeadlessUi.Run(() => Stats(HandCursors.Render(grab, style)));
        Assert.Equal(32 * 32, total);
        Assert.True(corner);
        Assert.InRange(opaque, total * 0.25, total * 0.75);   // 손이 칸의 상당 부분을 차지(빈 그림·가득 찬 그림 방지)
    }

    [Fact]
    public void Open_and_grab_differ()
    {
        var (a, b) = HeadlessUi.Run(() => (Stats(HandCursors.Render(false, HandCursorStyle.Classic)).Opaque, Stats(HandCursors.Render(true, HandCursorStyle.Classic)).Opaque));
        Assert.NotEqual(a, b);
    }

    /// <summary>시안 비교 그림을 docs/design/hand-cursor-options.png로 만든다(환경 변수 SEDOL_WRITE_CURSOR_PREVIEW=출력 경로일 때만).</summary>
    [Fact]
    public void Write_preview_sheet_when_requested()
    {
        var outPath = Environment.GetEnvironmentVariable("SEDOL_WRITE_CURSOR_PREVIEW");
        if (string.IsNullOrEmpty(outPath)) return;
        HeadlessUi.Run(() =>
        {
            const int cellW = 300, cellH = 330, pad = 16;
            var styles = new[] { (HandCursorStyle.Classic, "A  Classic (default)"), (HandCursorStyle.Dark, "B  Dark"), (HandCursorStyle.Navy, "C  Navy + crease") };
            var sheet = new RenderTargetBitmap(new PixelSize(cellW * 3, cellH * 2 + 60), new Vector(96, 96));
            using (var ctx = sheet.CreateDrawingContext())
            {
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)), new Rect(0, 0, sheet.Size.Width, sheet.Size.Height));
                var tf = new Typeface("Segoe UI");
                void Text(string s, double x, double y, double size, IBrush b)
                {
                    var ft = new FormattedText(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, b);
                    ctx.DrawText(ft, new Point(x, y));
                }
                Text("SedolPDF hand cursor options — 32px bitmap, shown at 1x (actual) and 5x", pad, 14, 15, Brushes.Black);
                for (int c = 0; c < 3; c++)
                    for (int r = 0; r < 2; r++)
                    {
                        double x0 = c * cellW, y0 = 60 + r * cellH;
                        // 밝은 문서 바탕(위)과 어두운 바탕(아래 반)에서 모두 확인
                        ctx.FillRectangle(Brushes.White, new Rect(x0 + pad, y0, cellW - 2 * pad, 200));
                        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x40)), new Rect(x0 + pad, y0 + 200, cellW - 2 * pad, 80));
                        using var bmp = HandCursors.Render(r == 1, styles[c].Item1);
                        var big = new Rect(x0 + pad + (cellW - 2 * pad - 160) / 2, y0 + 20, 160, 160);
                        ctx.DrawImage(bmp, new Rect(0, 0, 32, 32), big);
                        ctx.DrawImage(bmp, new Rect(0, 0, 32, 32), new Rect(x0 + pad + 40, y0 + 218, 32, 32));
                        using var bmp2 = HandCursors.Render(r == 1, styles[c].Item1);
                        ctx.DrawImage(bmp2, new Rect(0, 0, 32, 32), new Rect(x0 + pad + 100, y0 + 218, 32, 32));
                        Text(r == 0 ? "open (idle)" : "grab (dragging)", x0 + pad, y0 + 286, 13, Brushes.Black);
                        if (r == 0) Text(styles[c].Item2, x0 + pad, y0 - 22, 15, Brushes.Black);
                    }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            #pragma warning disable CS0618
            sheet.Save(outPath);
#pragma warning restore CS0618
            return 0;
        });
    }
}
