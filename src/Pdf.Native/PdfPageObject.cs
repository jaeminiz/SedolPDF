using System.Text;
using Pdf.Native.Interop;

namespace Pdf.Native;

/// <summary>PDF 행렬 [a b c d e f] (점 p → (a·x + c·y + e, b·x + d·y + f)).</summary>
public readonly record struct PdfMatrix(float A, float B, float C, float D, float E, float F)
{
    public static PdfMatrix Identity { get; } = new(1, 0, 0, 1, 0, 0);

    public static PdfMatrix Translate(float dx, float dy) => new(1, 0, 0, 1, dx, dy);

    /// <summary>(cx, cy)를 고정점으로 가로 sx, 세로 sy배.</summary>
    public static PdfMatrix ScaleAbout(float sx, float sy, float cx, float cy) => new(sx, 0, 0, sy, cx - sx * cx, cy - sy * cy);

    /// <summary>this를 먼저, 그다음 then을 적용하는 행렬 (PDF의 this × then).</summary>
    public PdfMatrix Then(PdfMatrix then) => new(
        A * then.A + B * then.C, A * then.B + B * then.D,
        C * then.A + D * then.C, C * then.B + D * then.D,
        E * then.A + F * then.C + then.E, E * then.B + F * then.D + then.F);

    public PdfPointF Apply(PdfPointF p) => new(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);

    internal FsMatrix ToNative() => new() { A = A, B = B, C = C, D = D, E = E, F = F };
    internal static PdfMatrix FromNative(FsMatrix m) => new(m.A, m.B, m.C, m.D, m.E, m.F);
}

public enum PdfPageObjectType { Unknown = 0, Text = 1, Path = 2, Image = 3, Shading = 4, Form = 5 }

/// <summary>PDF 색(0~255)과 알파(0~255).</summary>
public readonly record struct PdfRgba(byte R, byte G, byte B, byte A);

/// <summary>
/// 쪽 객체 하나. 그 객체가 든 쪽 핸들이 살아 있는 동안만 쓴다(쪽을 닫으면 <see cref="ObjectDisposedException"/>).
/// 쪽에 넣기 전의 새 객체(<see cref="PdfPage.CreateTextLike"/> 등)는 넣을 때 쪽이 소유하고, 넣지 않으면 <see cref="DestroyIfDetached"/>로 버린다.
/// 모든 멤버는 PDFium 전용 스레드에서만 부른다.
/// </summary>
public sealed unsafe class PdfPageObject
{
    private readonly PdfiumThread _t;
    private readonly PdfPage _page;
    private readonly IntPtr _h;
    private bool _attached;
    private bool _destroyed;

    internal PdfPageObject(PdfiumThread t, PdfPage page, IntPtr handle, bool attached)
    {
        _t = t;
        _page = page;
        _h = handle;
        _attached = attached;
    }

    internal IntPtr Handle
    {
        get
        {
            _t.AssertOnThread();
            ObjectDisposedException.ThrowIf(_destroyed || (_attached && !_page.IsAlive), this);
            return _h;
        }
    }

    internal void MarkAttached() => _attached = true;

    public bool IsAttached { get { _t.AssertOnThread(); return _attached; } }

    public PdfPageObjectType Type
    {
        get
        {
            int t = NativeMethods.FPDFPageObj_GetType(Handle);
            return t is >= 1 and <= 5 ? (PdfPageObjectType)t : PdfPageObjectType.Unknown;
        }
    }

    /// <summary>쪽 좌표(pt)의 경계 상자.</summary>
    public PdfRectF Bounds
    {
        get
        {
            float l, b, r, t;
            if (NativeMethods.FPDFPageObj_GetBounds(Handle, &l, &b, &r, &t) == 0) return default;
            return new PdfRectF(l, b, r, t);
        }
    }

    /// <summary>회전을 반영한 네 점 경계(텍스트·이미지 등). 얻지 못하면 Bounds의 사각형.</summary>
    public PdfQuadF RotatedBounds
    {
        get
        {
            float* q = stackalloc float[8];
            if (NativeMethods.FPDFPageObj_GetRotatedBounds(Handle, q) == 0) return PdfQuadF.FromRect(Bounds);
            // PDFium 순서: (x1,y1) 왼쪽 아래부터 시계 반대 방향 — 우리 PdfQuadF(P1 왼쪽 위, P2 오른쪽 위, P3 왼쪽 아래, P4 오른쪽 아래)로 맞춘다
            return new PdfQuadF(new(q[6], q[7]), new(q[4], q[5]), new(q[0], q[1]), new(q[2], q[3]));
        }
    }

    public PdfMatrix Matrix
    {
        get
        {
            FsMatrix m;
            if (NativeMethods.FPDFPageObj_GetMatrix(Handle, &m) == 0) throw new PdfiumException(0, "GetMatrix failed");
            return PdfMatrix.FromNative(m);
        }
        set
        {
            var m = value.ToNative();
            if (NativeMethods.FPDFPageObj_SetMatrix(Handle, &m) == 0) throw new PdfiumException(0, "SetMatrix failed");
        }
    }

    /// <summary>false면 그리기·내용 생성에서 빠진다(객체는 메모리에 남음, 번호 그대로).</summary>
    public bool IsActive
    {
        get { int a; return NativeMethods.FPDFPageObj_GetIsActive(Handle, &a) == 0 || a != 0; }
        set { if (NativeMethods.FPDFPageObj_SetIsActive(Handle, value ? 1 : 0) == 0) throw new PdfiumException(0, "SetIsActive failed"); }
    }

    /// <summary>쪽 좌표에서 tr을 객체 뒤에 곱한다(옮기기·크기 바꾸기). 객체의 새 행렬 = 원래 × tr.</summary>
    public void TransformBy(PdfMatrix tr) => Matrix = Matrix.Then(tr);

    public PdfRgba? FillColor
    {
        get { uint r, g, b, a; return NativeMethods.FPDFPageObj_GetFillColor(Handle, &r, &g, &b, &a) == 0 ? null : new((byte)r, (byte)g, (byte)b, (byte)a); }
        set { var c = value ?? new PdfRgba(0, 0, 0, 0); NativeMethods.FPDFPageObj_SetFillColor(Handle, c.R, c.G, c.B, c.A); }
    }

    public PdfRgba? StrokeColor
    {
        get { uint r, g, b, a; return NativeMethods.FPDFPageObj_GetStrokeColor(Handle, &r, &g, &b, &a) == 0 ? null : new((byte)r, (byte)g, (byte)b, (byte)a); }
        set { var c = value ?? new PdfRgba(0, 0, 0, 0); NativeMethods.FPDFPageObj_SetStrokeColor(Handle, c.R, c.G, c.B, c.A); }
    }

    public float StrokeWidth
    {
        get { float w; return NativeMethods.FPDFPageObj_GetStrokeWidth(Handle, &w) == 0 ? 0 : w; }
        set => NativeMethods.FPDFPageObj_SetStrokeWidth(Handle, value);
    }

    /// <summary>경로 객체의 채우기·선 그리기 방식.</summary>
    public void SetDrawMode(bool fill, bool stroke) =>
        NativeMethods.FPDFPath_SetDrawMode(Handle, fill ? NativeMethods.FPDF_FILLMODE_WINDING : NativeMethods.FPDF_FILLMODE_NONE, stroke ? 1 : 0);

    // ---- 텍스트 ----

    public float FontSize { get { float s; return NativeMethods.FPDFTextObj_GetFontSize(Handle, &s) == 0 ? 0 : s; } }

    public int TextRenderMode => NativeMethods.FPDFTextObj_GetTextRenderMode(Handle);

    /// <summary>글꼴 기본 이름(서브셋 접두사 포함)과 임베드 여부. 텍스트 객체가 아니면 null.</summary>
    public (string BaseName, bool Embedded)? Font
    {
        get
        {
            var f = NativeMethods.FPDFTextObj_GetFont(Handle);
            if (f == IntPtr.Zero) return null;
            nuint n = NativeMethods.FPDFFont_GetBaseFontName(f, null, 0);
            string name = "";
            if (n > 0)
            {
                var buf = new byte[(int)n];
                fixed (byte* p = buf) NativeMethods.FPDFFont_GetBaseFontName(f, p, n);
                name = Encoding.UTF8.GetString(buf).TrimEnd('\0');
            }
            return (name, NativeMethods.FPDFFont_GetIsEmbedded(f) != 0);
        }
    }

    /// <summary>텍스트 객체의 글. textPage는 같은 쪽에서 만든 것. 텍스트 객체가 아니면 null.</summary>
    public string? GetText(PdfTextPage textPage)
    {
        var tp = textPage.Handle;
        nuint n = NativeMethods.FPDFTextObj_GetText(Handle, tp, null, 0);
        if (n == 0) return null;
        if (n <= 2) return "";
        var buf = new ushort[(int)n / 2];
        fixed (ushort* p = buf) NativeMethods.FPDFTextObj_GetText(Handle, tp, p, n);
        var sb = new StringBuilder(buf.Length);
        foreach (var c in buf) { if (c == 0) break; sb.Append((char)c); }
        return sb.ToString();
    }

    /// <summary>텍스트 객체의 글을 바꾼다. 글꼴에 없는 글자는 PDFium이 빼므로 결과는 <see cref="GetText"/>로 확인한다.</summary>
    public bool SetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var buf = new ushort[text.Length + 1];
        for (int i = 0; i < text.Length; i++) buf[i] = text[i];
        fixed (ushort* p = buf) return NativeMethods.FPDFText_SetText(Handle, p) != 0;
    }

    // ---- 폼 ----

    /// <summary>폼 XObject의 자식들(읽기용 — V15: 자식 편집은 파일에 남지 않는다). 폼이 아니면 빈 목록.</summary>
    public IReadOnlyList<PdfPageObject> FormChildren
    {
        get
        {
            var h = Handle;
            int n = NativeMethods.FPDFFormObj_CountObjects(h);
            var list = new List<PdfPageObject>(Math.Max(0, n));
            for (int i = 0; i < n; i++)
            {
                var c = NativeMethods.FPDFFormObj_GetObject(h, (nuint)i);
                if (c != IntPtr.Zero) list.Add(new PdfPageObject(_t, _page, c, attached: true));
            }
            return list;
        }
    }

    // ---- 경로 읽기 ----

    /// <summary>
    /// 경로 객체의 모양(쪽 좌표 pt, 객체 행렬 적용). 곡선은 선분으로 펴고, 열린/닫힌 부분 경로(Subpaths)를 나눠 담는다.
    /// 경로가 아니거나 선분이 maxSegments보다 많으면 null(호출한 쪽이 경계 상자로 대신한다).
    /// </summary>
    public PdfPathGeometry? ReadPathGeometry(int maxSegments)
    {
        var h = Handle;
        if (NativeMethods.FPDFPageObj_GetType(h) != NativeMethods.FPDF_PAGEOBJ_PATH) return null;
        int n = NativeMethods.FPDFPath_CountSegments(h);
        if (n <= 0 || n > maxSegments) return null;
        int fillMode, stroke;
        if (NativeMethods.FPDFPath_GetDrawMode(h, &fillMode, &stroke) == 0) return null;
        FsMatrix m;
        var mat = NativeMethods.FPDFPageObj_GetMatrix(h, &m) == 0 ? PdfMatrix.Identity : PdfMatrix.FromNative(m);

        var subs = new List<PdfSubpath>();
        var cur = new List<PdfPointF>();
        bool closed = false;
        PdfPointF last = default;
        Span<PdfPointF> bez = stackalloc PdfPointF[3];
        int bezN = 0;
        void Flush()
        {
            if (cur.Count > 0) subs.Add(new PdfSubpath(cur.ToArray(), closed));
            cur = [];
            closed = false;
        }
        for (int i = 0; i < n; i++)
        {
            var seg = NativeMethods.FPDFPath_GetPathSegment(h, i);
            if (seg == IntPtr.Zero) continue;
            float x, y;
            if (NativeMethods.FPDFPathSegment_GetPoint(seg, &x, &y) == 0) continue;
            var p = mat.Apply(new PdfPointF(x, y));
            switch (NativeMethods.FPDFPathSegment_GetType(seg))
            {
                case 2:   // MOVETO (FPDF_SEGMENT_MOVETO)
                    Flush();
                    cur.Add(p);
                    last = p;
                    bezN = 0;
                    break;
                case 0:   // LINETO
                    cur.Add(p);
                    last = p;
                    bezN = 0;
                    break;
                case 1:   // BEZIERTO — 점 3개가 한 곡선
                    bez[bezN++] = p;
                    if (bezN == 3)
                    {
                        for (int k = 1; k <= 8; k++)
                        {
                            float t = k / 8f, u = 1 - t;
                            cur.Add(new PdfPointF(
                                u * u * u * last.X + 3 * u * u * t * bez[0].X + 3 * u * t * t * bez[1].X + t * t * t * bez[2].X,
                                u * u * u * last.Y + 3 * u * u * t * bez[0].Y + 3 * u * t * t * bez[1].Y + t * t * t * bez[2].Y));
                        }
                        last = bez[2];
                        bezN = 0;
                    }
                    break;
            }
            if (NativeMethods.FPDFPathSegment_GetClose(seg) != 0) closed = true;
        }
        Flush();
        // 선 굵기: 객체 행렬의 크기 배율을 곱해 쪽 좌표로 (PDFium의 StrokeWidth는 경로 좌표계 기준)
        float w;
        float sw = NativeMethods.FPDFPageObj_GetStrokeWidth(h, &w) == 0 ? 0 : w;
        float scale = MathF.Sqrt(MathF.Abs(mat.A * mat.D - mat.B * mat.C));
        return new PdfPathGeometry(subs, fillMode != NativeMethods.FPDF_FILLMODE_NONE, stroke != 0, sw * (scale > 0 ? scale : 1f));
    }

    // ---- 경로 만들기 ----

    public void MoveTo(float x, float y) => NativeMethods.FPDFPath_MoveTo(Handle, x, y);
    public void LineTo(float x, float y) => NativeMethods.FPDFPath_LineTo(Handle, x, y);
    public void BezierTo(float x1, float y1, float x2, float y2, float x3, float y3) => NativeMethods.FPDFPath_BezierTo(Handle, x1, y1, x2, y2, x3, y3);
    public void ClosePath() => NativeMethods.FPDFPath_Close(Handle);

    /// <summary>쪽에 넣지 않은 새 객체를 버린다. 이미 넣었으면 아무것도 하지 않는다.</summary>
    public void DestroyIfDetached()
    {
        _t.AssertOnThread();
        if (_attached || _destroyed) return;
        _destroyed = true;
        NativeMethods.FPDFPageObj_Destroy(_h);
    }
}
