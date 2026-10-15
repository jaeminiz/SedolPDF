using System.Drawing;
using Pdf.Native.Interop;

namespace Pdf.Native;

public sealed class PdfPage : IDisposable
{
    private readonly PdfiumThread _t;
    private IntPtr _handle;

    internal PdfPage(PdfiumThread t, IntPtr handle)
    {
        _t = t;
        _handle = handle;
    }

    internal IntPtr Handle { get { Check(); return _handle; } }

    public SizeF SizePt
    {
        get
        {
            Check();
            return new SizeF(NativeMethods.FPDF_GetPageWidthF(_handle), NativeMethods.FPDF_GetPageHeightF(_handle));
        }
    }

    /// <summary>0~3 (90도 시계 방향 단위).</summary>
    public int Rotation
    {
        get { Check(); return NativeMethods.FPDFPage_GetRotation(_handle); }
        set
        {
            Check();
            if (value is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(value));
            NativeMethods.FPDFPage_SetRotation(_handle, value);
        }
    }

    /// <summary>페이지 경계 상자 (PDF 사용자 좌표, 회전 전).</summary>
    public unsafe RectangleF BoundsPt
    {
        get
        {
            Check();
            FsRectF r;
            if (NativeMethods.FPDF_GetPageBoundingBox(_handle, &r) == 0)
                throw PdfiumException.FromLastError("페이지 경계");
            return RectangleF.FromLTRB(r.Left, r.Bottom, r.Right, r.Top);
        }
    }

    public int AnnotationCount { get { Check(); return NativeMethods.FPDFPage_GetAnnotCount(_handle); } }

    public byte[] RenderTile(double scale, int tileX, int tileY, int tileW, int tileH, bool withAnnotations)
        => RenderTile(scale, tileX, tileY, tileW, tileH, new RenderOptions(withAnnotations));

    /// <summary>
    /// 타일 하나만 BGRA(흰 배경)로 그린다. tileX/tileY는 scale을 적용한 화면 좌표(픽셀)의 타일 왼쪽 위.
    /// 비트맵은 타일 크기만 할당한다. HiddenAnnotSubtypes는 렌더하는 동안만 Hidden 플래그를 붙이고 finally에서 원복한다.
    /// </summary>
    public byte[] RenderTile(double scale, int tileX, int tileY, int tileW, int tileH, RenderOptions options)
    {
        Check();
        var changed = new List<(int index, int original)>();
        try
        {
            if (options.WithAnnotations && options.HiddenAnnotSubtypes is { Count: > 0 } subtypes)
            {
                int n = NativeMethods.FPDFPage_GetAnnotCount(_handle);
                for (int i = 0; i < n; i++)
                {
                    var a = NativeMethods.FPDFPage_GetAnnot(_handle, i);
                    if (a == IntPtr.Zero) continue;
                    try
                    {
                        if (subtypes.Contains(NativeMethods.FPDFAnnot_GetSubtype(a)))
                        {
                            int f = NativeMethods.FPDFAnnot_GetFlags(a);
                            changed.Add((i, f));
                            NativeMethods.FPDFAnnot_SetFlags(a, f | NativeMethods.FPDFANNOT_FLAG_HIDDEN);
                        }
                    }
                    finally { NativeMethods.FPDFPage_CloseAnnot(a); }
                }
            }
            return RenderCore(scale, tileX, tileY, tileW, tileH, options.WithAnnotations);
        }
        finally
        {
            foreach (var (index, original) in changed)
            {
                var a = NativeMethods.FPDFPage_GetAnnot(_handle, index);
                if (a == IntPtr.Zero) continue;
                NativeMethods.FPDFAnnot_SetFlags(a, original);
                NativeMethods.FPDFPage_CloseAnnot(a);
            }
        }
    }

    private unsafe byte[] RenderCore(double scale, int tileX, int tileY, int tileW, int tileH, bool withAnnotations)
    {
        var buffer = new byte[checked(tileW * tileH * 4)];
        fixed (byte* p = buffer)
        {
            IntPtr bmp = NativeMethods.FPDFBitmap_CreateEx(tileW, tileH, NativeMethods.FPDFBitmap_BGRA, (IntPtr)p, tileW * 4);
            if (bmp == IntPtr.Zero) throw new PdfiumException(0, "비트맵 생성 실패");
            try
            {
                NativeMethods.FPDFBitmap_FillRect(bmp, 0, 0, tileW, tileH, 0xFFFFFFFF);
                var m = new FsMatrix { A = (float)scale, D = (float)scale, E = -tileX, F = -tileY };
                var clip = new FsRectF { Left = 0, Top = 0, Right = tileW, Bottom = tileH };
                NativeMethods.FPDF_RenderPageBitmapWithMatrix(bmp, _handle, &m, &clip,
                    withAnnotations ? NativeMethods.FPDF_ANNOT : 0);
            }
            finally { NativeMethods.FPDFBitmap_Destroy(bmp); }
        }
        return buffer;
    }

    private void Check()
    {
        _t.AssertOnThread();
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        var h = _handle;
        _handle = IntPtr.Zero;
        PdfDocument.RunOnThread(_t, () => NativeMethods.FPDF_ClosePage(h));
    }
}
