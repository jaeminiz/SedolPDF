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

    public SizeF SizePt
    {
        get
        {
            Check();
            return new SizeF(NativeMethods.FPDF_GetPageWidthF(_handle), NativeMethods.FPDF_GetPageHeightF(_handle));
        }
    }

    /// <summary>
    /// 타일 하나만 BGRA(흰 배경)로 그린다. tileX/tileY는 scale을 적용한 화면 좌표(픽셀)의 타일 왼쪽 위.
    /// 비트맵은 타일 크기만 할당한다.
    /// </summary>
    public unsafe byte[] RenderTile(double scale, int tileX, int tileY, int tileW, int tileH, bool withAnnotations)
    {
        Check();
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
