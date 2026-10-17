using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Pdf.Native;

namespace PdfEditor.Appearance.Fonts;

/// <summary>
/// 글리프 윤곽선(em 단위, 1.0 = 글자 크기, y는 위쪽이 +). 화면이 PDF와 같은 글리프를 그리게 한다(A5).
/// HarfBuzzSharp 8.3.1.3에는 그리기 API의 관리 래퍼가 없어서(V10) 같은 패키지의 네이티브
/// libHarfBuzzSharp.dll이 내보내는 hb_font_draw_glyph + hb_draw_funcs_*를 직접 P/Invoke한다.
/// 결과는 AppFont 안에 gid별로 캐시한다.
/// </summary>
public static unsafe partial class GlyphOutlines
{
    private const string Lib = "libHarfBuzzSharp";

    [LibraryImport(Lib)] private static partial IntPtr hb_draw_funcs_create();
    [LibraryImport(Lib)] private static partial void hb_draw_funcs_make_immutable(IntPtr funcs);
    [LibraryImport(Lib)] private static partial void hb_draw_funcs_set_move_to_func(IntPtr funcs, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, float, float, IntPtr, void> f, IntPtr userData, IntPtr destroy);
    [LibraryImport(Lib)] private static partial void hb_draw_funcs_set_line_to_func(IntPtr funcs, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, float, float, IntPtr, void> f, IntPtr userData, IntPtr destroy);
    [LibraryImport(Lib)] private static partial void hb_draw_funcs_set_quadratic_to_func(IntPtr funcs, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, float, float, float, float, IntPtr, void> f, IntPtr userData, IntPtr destroy);
    [LibraryImport(Lib)] private static partial void hb_draw_funcs_set_cubic_to_func(IntPtr funcs, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, float, float, float, float, float, float, IntPtr, void> f, IntPtr userData, IntPtr destroy);
    [LibraryImport(Lib)] private static partial void hb_draw_funcs_set_close_path_func(IntPtr funcs, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void> f, IntPtr userData, IntPtr destroy);
    [LibraryImport(Lib)] private static partial void hb_font_draw_glyph(IntPtr font, uint glyph, IntPtr funcs, IntPtr drawData);

    private static readonly Lazy<IntPtr> Funcs = new(CreateFuncs);

    private sealed class Collector(float unitsPerEm)
    {
        public readonly List<PathSeg> Segs = [];
        public readonly float Scale = 1f / unitsPerEm;
        public PdfPointF Current;
        public PdfPointF Start;
        public bool Open;
    }

    /// <summary>글리프 윤곽선. 빈 글리프(공백)는 빈 배열.</summary>
    public static ImmutableArray<PathSeg> Get(AppFont font, int gid)
    {
        if (font.OutlineCache.TryGetValue(gid, out var cached)) return cached;
        var result = font.WithNativeFont(handle =>
        {
            var c = new Collector(font.UnitsPerEm);
            var gch = GCHandle.Alloc(c);
            try
            {
                hb_font_draw_glyph(handle, (uint)gid, Funcs.Value, GCHandle.ToIntPtr(gch));
            }
            finally { gch.Free(); }
            // 닫지 않은 윤곽선이 남으면 닫는다.
            if (c.Open) c.Segs.Add(new ClosePath());
            return c.Segs.ToImmutableArray();
        });
        return font.OutlineCache.GetOrAdd(gid, result);
    }

    private static IntPtr CreateFuncs()
    {
        IntPtr f = hb_draw_funcs_create();
        if (f == IntPtr.Zero) throw new InvalidOperationException("hb_draw_funcs_create failed");
        hb_draw_funcs_set_move_to_func(f, &MoveToCb, IntPtr.Zero, IntPtr.Zero);
        hb_draw_funcs_set_line_to_func(f, &LineToCb, IntPtr.Zero, IntPtr.Zero);
        hb_draw_funcs_set_quadratic_to_func(f, &QuadToCb, IntPtr.Zero, IntPtr.Zero);
        hb_draw_funcs_set_cubic_to_func(f, &CubicToCb, IntPtr.Zero, IntPtr.Zero);
        hb_draw_funcs_set_close_path_func(f, &CloseCb, IntPtr.Zero, IntPtr.Zero);
        hb_draw_funcs_make_immutable(f);
        return f;   // 프로세스 수명 동안 유지
    }

    private static Collector C(IntPtr drawData) => (Collector)GCHandle.FromIntPtr(drawData).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MoveToCb(IntPtr funcs, IntPtr data, IntPtr st, float x, float y, IntPtr ud)
    {
        var c = C(data);
        if (c.Open) c.Segs.Add(new ClosePath());
        var p = new PdfPointF(x * c.Scale, y * c.Scale);
        c.Segs.Add(new MoveTo(p));
        c.Current = c.Start = p;
        c.Open = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void LineToCb(IntPtr funcs, IntPtr data, IntPtr st, float x, float y, IntPtr ud)
    {
        var c = C(data);
        var p = new PdfPointF(x * c.Scale, y * c.Scale);
        c.Segs.Add(new LineTo(p));
        c.Current = p;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void QuadToCb(IntPtr funcs, IntPtr data, IntPtr st, float cx, float cy, float x, float y, IntPtr ud)
    {
        var c = C(data);
        var q = new PdfPointF(cx * c.Scale, cy * c.Scale);
        var p = new PdfPointF(x * c.Scale, y * c.Scale);
        var p0 = c.Current;
        // 2차 -> 3차 베지어
        var c1 = new PdfPointF(p0.X + 2f / 3f * (q.X - p0.X), p0.Y + 2f / 3f * (q.Y - p0.Y));
        var c2 = new PdfPointF(p.X + 2f / 3f * (q.X - p.X), p.Y + 2f / 3f * (q.Y - p.Y));
        c.Segs.Add(new CubicTo(c1, c2, p));
        c.Current = p;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CubicToCb(IntPtr funcs, IntPtr data, IntPtr st, float x1, float y1, float x2, float y2, float x, float y, IntPtr ud)
    {
        var c = C(data);
        var p = new PdfPointF(x * c.Scale, y * c.Scale);
        c.Segs.Add(new CubicTo(new PdfPointF(x1 * c.Scale, y1 * c.Scale), new PdfPointF(x2 * c.Scale, y2 * c.Scale), p));
        c.Current = p;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CloseCb(IntPtr funcs, IntPtr data, IntPtr st, IntPtr ud)
    {
        var c = C(data);
        c.Segs.Add(new ClosePath());
        c.Current = c.Start;
        c.Open = false;
    }
}
