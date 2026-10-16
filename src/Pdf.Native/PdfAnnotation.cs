using Pdf.Native.Interop;

namespace Pdf.Native;

/// <summary>
/// PDFium 주석 하나(FPDF_ANNOTATION). PdfiumThread 전용 스레드에서만 쓴다.
/// PdfPage보다 먼저 Dispose해야 한다.
/// </summary>
public sealed unsafe class PdfAnnotation : IDisposable
{
    private readonly PdfiumThread _t;
    private IntPtr _handle;

    internal PdfAnnotation(PdfiumThread t, IntPtr handle)
    {
        _t = t;
        _handle = handle;
    }

    internal IntPtr Handle { get { Check(); return _handle; } }

    public int Subtype { get { Check(); return NativeMethods.FPDFAnnot_GetSubtype(_handle); } }

    public int Flags
    {
        get { Check(); return NativeMethods.FPDFAnnot_GetFlags(_handle); }
        set
        {
            Check();
            if (NativeMethods.FPDFAnnot_SetFlags(_handle, value) == 0) throw PdfiumException.FromLastError("주석 플래그 쓰기");
        }
    }

    public PdfRectF Rect
    {
        get
        {
            Check();
            FsRectF r;
            if (NativeMethods.FPDFAnnot_GetRect(_handle, &r) == 0) throw PdfiumException.FromLastError("주석 영역 읽기");
            return new PdfRectF(r.Left, r.Bottom, r.Right, r.Top);
        }
        set
        {
            Check();
            var r = new FsRectF { Left = value.Left, Top = value.Top, Right = value.Right, Bottom = value.Bottom };
            if (NativeMethods.FPDFAnnot_SetRect(_handle, &r) == 0) throw PdfiumException.FromLastError("주석 영역 쓰기");
        }
    }

    public bool HasKey(string key)
    {
        Check();
        return NativeMethods.FPDFAnnot_HasKey(_handle, key) != 0;
    }

    /// <summary>키가 없거나 문자열·이름이 아니면 null. 이름 객체(/Name /Comment)도 읽힌다.</summary>
    public string? GetString(string key)
    {
        Check();
        var h = _handle;
        if (NativeMethods.FPDFAnnot_HasKey(h, key) == 0) return null;
        if (NativeMethods.FPDFAnnot_GetStringValue(h, key, IntPtr.Zero, 0) == 0) return null;
        return NativeUtf16.Read((b, n) => NativeMethods.FPDFAnnot_GetStringValue(h, key, b, (uint)n));
    }

    public void SetString(string key, string value)
    {
        Check();
        // C# 문자열은 메모리에서 NUL로 끝나므로 고정(fixed)만 하면 UTF-16LE NUL 종료 버퍼가 된다.
        fixed (char* p = value)
            if (NativeMethods.FPDFAnnot_SetStringValue(_handle, key, p) == 0)
                throw PdfiumException.FromLastError($"주석 문자열({key}) 쓰기");
    }

    public float? GetNumber(string key)
    {
        Check();
        float v;
        return NativeMethods.FPDFAnnot_GetNumberValue(_handle, key, &v) != 0 ? v : null;
    }

    /// <summary>
    /// opacity는 /CA가 된다(A = round(opacity × 255)). AP가 있으면 PDFium이 실패하므로
    /// <see cref="InvalidOperationException"/>을 던진다 — SetAppearance(null)로 AP를 먼저 지운다.
    /// </summary>
    public void SetColor(PdfColorType type, byte r, byte g, byte b, float opacity)
    {
        Check();
        uint a = (uint)Math.Clamp((int)Math.Round(opacity * 255f), 0, 255);
        if (NativeMethods.FPDFAnnot_SetColor(_handle, (int)type, r, g, b, a) != 0) return;
        if (GetAppearance() is not null)
            throw new InvalidOperationException("AP가 있는 주석은 색을 바꿀 수 없습니다. AP를 먼저 지우세요(SetAppearance(null)).");
        throw PdfiumException.FromLastError("주석 색 쓰기");
    }

    /// <summary>/Border [0 0 width].</summary>
    public void SetBorderWidth(float width)
    {
        Check();
        if (NativeMethods.FPDFAnnot_SetBorder(_handle, 0, 0, width) == 0) throw PdfiumException.FromLastError("주석 테두리 쓰기");
    }

    public IReadOnlyList<PdfQuadF> GetQuads()
    {
        Check();
        int n = (int)NativeMethods.FPDFAnnot_CountAttachmentPoints(_handle);
        var list = new List<PdfQuadF>(n);
        for (int i = 0; i < n; i++)
        {
            FsQuadPointsF q;
            if (NativeMethods.FPDFAnnot_GetAttachmentPoints(_handle, (nuint)i, &q) == 0)
                throw PdfiumException.FromLastError("주석 쿼드 읽기");
            list.Add(new PdfQuadF(new(q.X1, q.Y1), new(q.X2, q.Y2), new(q.X3, q.Y3), new(q.X4, q.Y4)));
        }
        return list;
    }

    /// <summary>기존 개수만큼 덮어쓰고 남는 것은 덧붙인다. 기존보다 적게는 못 줄인다(PDFium에 삭제 API 없음).</summary>
    public void SetQuads(IReadOnlyList<PdfQuadF> quads)
    {
        Check();
        int existing = (int)NativeMethods.FPDFAnnot_CountAttachmentPoints(_handle);
        if (quads.Count < existing)
            throw new NotSupportedException($"쿼드를 {existing}개에서 {quads.Count}개로 줄일 수 없습니다(PDFium에 삭제 API가 없음).");
        for (int i = 0; i < quads.Count; i++)
        {
            var s = quads[i];
            var q = new FsQuadPointsF
            {
                X1 = s.P1.X, Y1 = s.P1.Y, X2 = s.P2.X, Y2 = s.P2.Y,
                X3 = s.P3.X, Y3 = s.P3.Y, X4 = s.P4.X, Y4 = s.P4.Y,
            };
            int ok = i < existing
                ? NativeMethods.FPDFAnnot_SetAttachmentPoints(_handle, (nuint)i, &q)
                : NativeMethods.FPDFAnnot_AppendAttachmentPoints(_handle, &q);
            if (ok == 0) throw PdfiumException.FromLastError("주석 쿼드 쓰기");
        }
    }

    public IReadOnlyList<IReadOnlyList<PdfPointF>> GetInkStrokes()
    {
        Check();
        int n = (int)NativeMethods.FPDFAnnot_GetInkListCount(_handle);
        var strokes = new List<IReadOnlyList<PdfPointF>>(n);
        for (int i = 0; i < n; i++)
        {
            uint count = NativeMethods.FPDFAnnot_GetInkListPath(_handle, (uint)i, null, 0);
            var buf = new FsPointF[count];
            if (count > 0)
                fixed (FsPointF* p = buf)
                    NativeMethods.FPDFAnnot_GetInkListPath(_handle, (uint)i, p, count);
            strokes.Add(buf.Select(pt => new PdfPointF(pt.X, pt.Y)).ToArray());
        }
        return strokes;
    }

    /// <summary>기존 InkList를 지우고 획마다 다시 추가한다.</summary>
    public void SetInkStrokes(IReadOnlyList<IReadOnlyList<PdfPointF>> strokes)
    {
        Check();
        foreach (var s in strokes)
            if (s.Count == 0) throw new ArgumentException("점이 없는 획이 있습니다.", nameof(strokes));
        NativeMethods.FPDFAnnot_RemoveInkList(_handle);   // InkList가 없으면 false — 무시
        foreach (var s in strokes)
        {
            var pts = new FsPointF[s.Count];
            for (int i = 0; i < pts.Length; i++) pts[i] = new FsPointF { X = s[i].X, Y = s[i].Y };
            fixed (FsPointF* p = pts)
                if (NativeMethods.FPDFAnnot_AddInkStroke(_handle, p, (nuint)pts.Length) < 0)
                    throw PdfiumException.FromLastError("잉크 획 쓰기");
        }
    }

    /// <summary>Normal AP 내용 스트림을 쓴다. null이면 모든 모드의 AP를 지운다.</summary>
    public void SetAppearance(string? content)
    {
        Check();
        int ok;
        if (content is null) ok = NativeMethods.FPDFAnnot_SetAP(_handle, NativeMethods.FPDF_ANNOT_APPEARANCEMODE_NORMAL, null);
        else
            fixed (char* p = content)
                ok = NativeMethods.FPDFAnnot_SetAP(_handle, NativeMethods.FPDF_ANNOT_APPEARANCEMODE_NORMAL, p);
        if (ok == 0) throw PdfiumException.FromLastError("주석 AP 쓰기");
    }

    public string? GetAppearance()
    {
        Check();
        var h = _handle;
        // AP가 없으면 PDFium은 빈 문자열 길이(NUL 2바이트)를 돌려준다
        if (NativeMethods.FPDFAnnot_GetAP(h, NativeMethods.FPDF_ANNOT_APPEARANCEMODE_NORMAL, IntPtr.Zero, 0) <= 2) return null;
        return NativeUtf16.Read((b, n) => NativeMethods.FPDFAnnot_GetAP(h, NativeMethods.FPDF_ANNOT_APPEARANCEMODE_NORMAL, b, (uint)n));
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
        PdfDocument.RunOnThread(_t, () => NativeMethods.FPDFPage_CloseAnnot(h));
    }
}
