using System.Drawing;
using Pdf.Native.Interop;

namespace Pdf.Native;

public sealed class PdfPage : IDisposable
{
    private readonly PdfiumThread _t;
    private IntPtr _handle;

    private readonly PdfDocument? _doc;

    internal PdfPage(PdfiumThread t, IntPtr handle, PdfDocument? doc = null)
    {
        _t = t;
        _handle = handle;
        _doc = doc;
    }

    /// <summary>쪽 핸들이 아직 열려 있는가 (쪽 객체가 확인한다).</summary>
    internal bool IsAlive => _handle != IntPtr.Zero;

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

    public PdfTextPage LoadText()
    {
        Check();
        IntPtr tp = NativeMethods.FPDFText_LoadPage(_handle);
        if (tp == IntPtr.Zero) throw PdfiumException.FromLastError("텍스트 페이지");
        return new PdfTextPage(_t, tp);
    }

    public int AnnotationCount { get { Check(); return NativeMethods.FPDFPage_GetAnnotCount(_handle); } }

    public PdfAnnotation GetAnnotation(int index)
    {
        Check();
        var a = NativeMethods.FPDFPage_GetAnnot(_handle, index);
        if (a == IntPtr.Zero) throw PdfiumException.FromLastError("주석 불러오기");
        return new PdfAnnotation(_t, a);
    }

    /// <summary>주석 목록 맨 끝에 새 주석을 만든다 (PdfAnnotSubtype 값).</summary>
    public PdfAnnotation CreateAnnotation(int subtype)
    {
        Check();
        var a = NativeMethods.FPDFPage_CreateAnnot(_handle, subtype);
        if (a == IntPtr.Zero) throw new PdfiumException(0, $"주석 만들기 실패 (유형 {subtype})");
        return new PdfAnnotation(_t, a);
    }

    public void RemoveAnnotation(int index)
    {
        Check();
        if (NativeMethods.FPDFPage_RemoveAnnot(_handle, index) == 0) throw PdfiumException.FromLastError("주석 삭제");
    }

    /// <summary>주석의 key("Popup", "IRT")가 가리키는 주석의 인덱스. 없으면 null.</summary>
    public int? GetLinkedAnnotationIndex(int index, string key)
    {
        Check();
        var a = NativeMethods.FPDFPage_GetAnnot(_handle, index);
        if (a == IntPtr.Zero) throw PdfiumException.FromLastError("주석 불러오기");
        try
        {
            var linked = NativeMethods.FPDFAnnot_GetLinkedAnnot(a, key);
            if (linked == IntPtr.Zero) return null;
            try
            {
                int i = NativeMethods.FPDFPage_GetAnnotIndex(_handle, linked);
                return i < 0 ? null : i;
            }
            finally { NativeMethods.FPDFPage_CloseAnnot(linked); }
        }
        finally { NativeMethods.FPDFPage_CloseAnnot(a); }
    }

    public byte[] RenderTile(double scale, int tileX, int tileY, int tileW, int tileH, bool withAnnotations)
        => RenderTile(scale, tileX, tileY, tileW, tileH, new RenderOptions(withAnnotations));

    /// <summary>
    /// 타일 하나만 BGRA(흰 배경)로 그린다. tileX/tileY는 scale을 적용한 화면 좌표(픽셀)의 타일 왼쪽 위.
    /// 비트맵은 타일 크기만 할당한다. HiddenAnnotSubtypes·HiddenAnnotIndices는 렌더하는 동안만 Hidden 플래그를 붙이고 finally에서 원복한다.
    /// </summary>
    public byte[] RenderTile(double scale, int tileX, int tileY, int tileW, int tileH, RenderOptions options)
    {
        Check();
        var changed = new List<(int index, int original)>();
        try
        {
            var subtypes = options.HiddenAnnotSubtypes;
            var indices = options.HiddenAnnotIndices;
            if (options.WithAnnotations && (subtypes is { Count: > 0 } || indices is { Count: > 0 }))
            {
                int n = NativeMethods.FPDFPage_GetAnnotCount(_handle);
                for (int i = 0; i < n; i++)
                {
                    var a = NativeMethods.FPDFPage_GetAnnot(_handle, i);
                    if (a == IntPtr.Zero) continue;
                    try
                    {
                        if ((indices?.Contains(i) ?? false) || (subtypes?.Contains(NativeMethods.FPDFAnnot_GetSubtype(a)) ?? false))
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

    /// <summary>
    /// 장치 컨텍스트(프린터 HDC 등)에 인쇄용으로 그린다. (x, y, width, height)는 회전을 적용한 뒤의 장치 좌표 사각형.
    /// rotate: 0 정상, 1 시계 방향 90도. StartPage/EndPage는 호출자가 맡는다.
    /// </summary>
    public void RenderToDc(IntPtr hdc, int x, int y, int width, int height, int rotate, bool withAnnotations)
    {
        Check();
        if (hdc == IntPtr.Zero) throw new ArgumentException("HDC가 없습니다.", nameof(hdc));
        if (rotate is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(rotate));
        NativeMethods.FPDF_RenderPage(hdc, _handle, x, y, width, height, rotate,
            (withAnnotations ? NativeMethods.FPDF_ANNOT : 0) | NativeMethods.FPDF_PRINTING);
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

    // ---- 쪽 객체 (M4) ----

    public int ObjectCount { get { Check(); return NativeMethods.FPDFPage_CountObjects(_handle); } }

    /// <summary>쪽 객체(내용 스트림 순서). 쪽 핸들보다 오래 쥐지 않는다.</summary>
    public PdfPageObject GetObject(int index)
    {
        Check();
        var o = NativeMethods.FPDFPage_GetObject(_handle, index);
        if (o == IntPtr.Zero) throw new ArgumentOutOfRangeException(nameof(index));
        return new PdfPageObject(_t, this, o, attached: true);
    }

    private IntPtr DocHandle => (_doc ?? throw new InvalidOperationException("This page was not loaded from a PdfDocument.")).Handle;

    /// <summary>
    /// original과 같은 글꼴·크기·행렬·채우기 색·렌더 모드로 새 텍스트 객체를 만든다(쪽에는 아직 넣지 않음).
    /// 글꼴에 없는 글자는 빠지므로 결과 글을 확인해야 한다(V14). 실패하면 null.
    /// </summary>
    public PdfPageObject? CreateTextLike(PdfPageObject original, string text)
    {
        Check();
        var font = NativeMethods.FPDFTextObj_GetFont(original.Handle);
        if (font == IntPtr.Zero) return null;
        var h = NativeMethods.FPDFPageObj_CreateTextObj(DocHandle, font, original.FontSize);
        if (h == IntPtr.Zero) return null;
        var o = new PdfPageObject(_t, this, h, attached: false);
        if (!o.SetText(text)) { o.DestroyIfDetached(); return null; }
        o.Matrix = original.Matrix;
        if (original.FillColor is { } fc) o.FillColor = fc;
        if (original.StrokeColor is { } sc) o.StrokeColor = sc;
        NativeMethods.FPDFTextObj_SetTextRenderMode(h, original.TextRenderMode);
        return o;
    }

    /// <summary>BGRA(불투명 또는 알파) 그림으로 새 이미지 객체를 만든다. placement는 단위 사각형 → 쪽 좌표 행렬.</summary>
    public unsafe PdfPageObject CreateImage(byte[] bgra, int width, int height, PdfMatrix placement)
    {
        Check();
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) throw new ArgumentException("Bad bitmap size.", nameof(bgra));
        var h = NativeMethods.FPDFPageObj_NewImageObj(DocHandle);
        if (h == IntPtr.Zero) throw new PdfiumException(0, "NewImageObj failed");
        var o = new PdfPageObject(_t, this, h, attached: false);
        fixed (byte* p = bgra)
        {
            var bmp = NativeMethods.FPDFBitmap_CreateEx(width, height, NativeMethods.FPDFBitmap_BGRA, (IntPtr)p, width * 4);
            if (bmp == IntPtr.Zero) { o.DestroyIfDetached(); throw new PdfiumException(0, "Bitmap create failed"); }
            try
            {
                if (NativeMethods.FPDFImageObj_SetBitmap(null, 0, h, bmp) == 0) { o.DestroyIfDetached(); throw new PdfiumException(0, "SetBitmap failed"); }
            }
            finally { NativeMethods.FPDFBitmap_Destroy(bmp); }
        }
        o.Matrix = placement;
        return o;
    }

    /// <summary>(x, y)에서 시작하는 빈 경로 객체. MoveTo·LineTo·BezierTo·ClosePath로 채운 뒤 SetDrawMode.</summary>
    public PdfPageObject CreatePath(float x, float y)
    {
        Check();
        var h = NativeMethods.FPDFPageObj_CreateNewPath(x, y);
        if (h == IntPtr.Zero) throw new PdfiumException(0, "CreateNewPath failed");
        return new PdfPageObject(_t, this, h, attached: false);
    }

    /// <summary>새 객체를 index 자리(없으면 맨 뒤 = 맨 위)에 넣는다. 그 뒤로는 쪽이 소유한다.</summary>
    public void InsertObject(PdfPageObject o, int? index = null)
    {
        Check();
        if (o.IsAttached) throw new InvalidOperationException("The object is already on a page.");
        int at = index ?? ObjectCount;
        if (NativeMethods.FPDFPage_InsertObjectAtIndex(_handle, o.Handle, (nuint)at) == 0)
            throw new PdfiumException(0, "InsertObjectAtIndex failed");
        o.MarkAttached();
    }

    /// <summary>
    /// 쪽 내용 스트림을 지금 객체들로 다시 쓴다. **출력 사본에서만 부른다**(편집 중인 문서에서 부르면 객체 번호가 바뀌어
    /// 편집 기록 replay가 깨진다, M4 계획 C1).
    /// </summary>
    public void GenerateContent()
    {
        Check();
        if (NativeMethods.FPDFPage_GenerateContent(_handle) == 0) throw new PdfiumException(0, "GenerateContent failed");
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
