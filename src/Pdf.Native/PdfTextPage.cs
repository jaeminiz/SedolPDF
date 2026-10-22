using System.Drawing;
using Pdf.Native.Interop;

namespace Pdf.Native;

public sealed record PdfTextMatch(int CharIndex, int CharCount);

/// <summary>글자 하나: PDFium 글자 번호, 글자(문자열), 상자(PDF 사용자 좌표: X=왼쪽, Y=아래, 높이는 위로).</summary>
public sealed record PdfTextChar(int Index, string Text, RectangleF Box);

public sealed unsafe class PdfTextPage : IDisposable
{
    private readonly PdfiumThread _t;
    private IntPtr _handle;

    internal PdfTextPage(PdfiumThread t, IntPtr handle)
    {
        _t = t;
        _handle = handle;
    }

    private IntPtr H { get { _t.AssertOnThread(); ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this); return _handle; } }

    internal IntPtr Handle => H;

    public int CharCount => NativeMethods.FPDFText_CountChars(H);

    public string GetText(int start, int count)
    {
        var h = H;
        if (count <= 0) return string.Empty;
        var buf = new ushort[count + 1];
        fixed (ushort* p = buf)
        {
            int got = NativeMethods.FPDFText_GetText(h, start, count, p);
            if (got <= 1) return string.Empty;
            return new string((char*)p, 0, got - 1);
        }
    }

    /// <summary>페이지 좌표(PDF 사용자 좌표, pt)의 글자 인덱스. 없으면 -1.</summary>
    public int GetCharIndexAtPos(double xPt, double yPt, double tolerancePt)
        => NativeMethods.FPDFText_GetCharIndexAtPos(H, xPt, yPt, tolerancePt, tolerancePt);

    /// <summary>글자 범위의 사각형들 (PDF 사용자 좌표).</summary>
    public IReadOnlyList<RectangleF> GetRects(int start, int count)
    {
        var h = H;
        int n = NativeMethods.FPDFText_CountRects(h, start, count);
        var list = new List<RectangleF>(Math.Max(n, 0));
        for (int i = 0; i < n; i++)
        {
            double l, t, r, b;
            if (NativeMethods.FPDFText_GetRect(h, i, &l, &t, &r, &b) == 0) continue;
            list.Add(RectangleF.FromLTRB((float)l, (float)b, (float)r, (float)t));
        }
        return list;
    }

    /// <summary>
    /// 글자 상자의 중심점이 사각형(PDF 사용자 좌표) 안에 있는 글자들. 줄바꿈 같은 제어 문자와 상자를 못 얻는 글자는 뺀다.
    /// 반환 순서는 PDFium 글자 번호 순서이다.
    /// </summary>
    public IReadOnlyList<PdfTextChar> GetCharsInRect(RectangleF rectPt)
    {
        var h = H;
        int n = NativeMethods.FPDFText_CountChars(h);
        var list = new List<PdfTextChar>();
        float l0 = Math.Min(rectPt.Left, rectPt.Right), r0 = Math.Max(rectPt.Left, rectPt.Right);
        float b0 = Math.Min(rectPt.Top, rectPt.Bottom), t0 = Math.Max(rectPt.Top, rectPt.Bottom);
        for (int i = 0; i < n; i++)
        {
            uint u = NativeMethods.FPDFText_GetUnicode(h, i);
            if (u < 0x20 || u == 0xFFFE || u == 0xFFFF) continue;
            double l, r, b, t;
            if (NativeMethods.FPDFText_GetCharBox(h, i, &l, &r, &b, &t) == 0) continue;
            double cx = (l + r) / 2, cy = (b + t) / 2;
            if (cx < l0 || cx > r0 || cy < b0 || cy > t0) continue;
            list.Add(new PdfTextChar(i, char.ConvertFromUtf32((int)(u > 0x10FFFF || (u >= 0xD800 && u <= 0xDFFF) ? 0xFFFD : u)),
                RectangleF.FromLTRB((float)l, (float)b, (float)r, (float)t)));
        }
        return list;
    }

    public IReadOnlyList<PdfTextMatch> FindAll(string query, bool matchCase, bool wholeWord)
    {
        var h = H;
        var result = new List<PdfTextMatch>();
        if (string.IsNullOrEmpty(query)) return result;
        // UTF-16 NUL 종료 버퍼. 검색 동안 고정한다.
        var buf = new ushort[query.Length + 1];
        for (int i = 0; i < query.Length; i++) buf[i] = query[i];
        ulong flags = (matchCase ? NativeMethods.FPDF_MATCHCASE : 0) | (wholeWord ? NativeMethods.FPDF_MATCHWHOLEWORD : 0);
        fixed (ushort* p = buf)
        {
            IntPtr sch = NativeMethods.FPDFText_FindStart(h, p, flags, 0);
            if (sch == IntPtr.Zero) return result;
            try
            {
                while (NativeMethods.FPDFText_FindNext(sch) != 0)
                    result.Add(new PdfTextMatch(NativeMethods.FPDFText_GetSchResultIndex(sch), NativeMethods.FPDFText_GetSchCount(sch)));
            }
            finally { NativeMethods.FPDFText_FindClose(sch); }
        }
        return result;
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        var h = _handle;
        _handle = IntPtr.Zero;
        PdfDocument.RunOnThread(_t, () => NativeMethods.FPDFText_ClosePage(h));
    }
}
