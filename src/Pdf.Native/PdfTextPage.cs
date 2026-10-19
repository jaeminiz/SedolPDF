using System.Drawing;
using Pdf.Native.Interop;

namespace Pdf.Native;

public sealed record PdfTextMatch(int CharIndex, int CharCount);

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
