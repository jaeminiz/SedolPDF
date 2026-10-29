using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PdfEditor.App.Viewer;

/// <summary>
/// 손 도구 커서: 펼친 손(대기)과 쥔 손(끄는 중). 아이콘과 같은 자작 손 윤곽(Icons.axaml의 Icon.hand 계열)을 비트맵으로 그려 만든다.
/// 그리지 못하는 환경(헤드리스 렌더러 없음 등)이면 표준 손 커서로 되돌아간다. 실제 화면의 모양·125/150% 배율 크기는 검증 필요.
/// </summary>
public static class HandCursors
{
    public const int SizePx = 32;
    public const string OpenPath = "F1 M7 18L5.2 14.6L2.9 11.2A1 1 0 0 1 4.5 10L5.8 11.4L5.8 5.2A1 1 0 0 1 7.8 5.2L7.8 8.5L8.4 8.5L8.4 3.2A1 1 0 0 1 10.4 3.2L10.4 8.5L11 8.5L11 4.2A1 1 0 0 1 13 4.2L13 8.5L13.6 8.5L13.6 6A1 1 0 0 1 15.6 6L15.6 14Q15.6 18 12 18Z";
    public const string GrabPath = "F1 M5 10.5A1.4 1.4 0 0 1 7.5 9A1.4 1.4 0 0 1 10 8.6A1.4 1.4 0 0 1 12.6 9A1.4 1.4 0 0 1 15.2 10.5L15.2 14Q15.2 18 11.5 18L8.5 18Q5 18 5 14Z";

    private static Cursor? _open, _grab;

    /// <summary>대기 중인 손(펼침).</summary>
    public static Cursor Open => _open ??= Make(OpenPath);

    /// <summary>끄는 중인 손(쥠).</summary>
    public static Cursor Grab => _grab ??= Make(GrabPath);

    /// <summary>비트맵 커서 두 개가 실제로 만들어졌는가(표준 커서로 되돌아갔으면 false).</summary>
    public static bool IsCustom => Open != StandardFallback && Grab != StandardFallback;

    private static readonly Cursor StandardFallback = new(StandardCursorType.Hand);

    private static Cursor Make(string path)
    {
        try
        {
            var geo = StreamGeometry.Parse(path);
            double scale = SizePx / 20.0;
            var bmp = new RenderTargetBitmap(new PixelSize(SizePx, SizePx), new Vector(96, 96));
            using (var ctx = bmp.CreateDrawingContext())
            {
                using var _ = ctx.PushTransform(Matrix.CreateScale(scale, scale));
                ctx.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 0.9, lineJoin: PenLineJoin.Round), geo);
            }
            return new Cursor(bmp, new PixelPoint(SizePx / 2, SizePx / 2));
        }
        catch (Exception)
        {
            return StandardFallback;
        }
    }
}
