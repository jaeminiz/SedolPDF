using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfEditor.TestFixtures;

/// <summary>
/// M4(객체 단위 편집) 시험용 PDF. 쪽 내용(텍스트·경로·이미지·폼)을 직접 다룬다.
/// 일반 문서형과 도면형(폼 XObject·클리핑·점선·투명도·그러데이션)을 만든다.
/// </summary>
public static class ContentPdfFactory
{
    static ContentPdfFactory() => System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TestPdfFactory).TypeHandle);   // 글꼴 해석기 등록

    /// <summary>일반 문서형 A4 1쪽의 객체 이름(쪽 내용 순서와 무관, 시험에서 텍스트로 찾는다).</summary>
    public const string LineArial = "Arial line ABC 123";
    public const string LineKorean = "가나다 도면 검토";
    public const string LineHelvetica = "Helvetica standard";

    /// <summary>
    /// A4 세로 1쪽: Arial 한 줄(TrueType 서브셋), 맑은 고딕 한글 한 줄(CID, Identity-H), 표준 Helvetica 한 줄(임베드 없음),
    /// PNG 이미지 하나, 빨간 사각형 하나, 파란 선 하나. stripToUnicode면 맑은 고딕 글꼴의 /ToUnicode를 지운다.
    /// </summary>
    public static void CreateDocumentLike(string path, bool stripToUnicode = false, int pages = 1)
    {
        using (var doc = new PdfDocument())
        {
            for (int pn = 0; pn < pages; pn++)
            {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            using (var g = XGraphics.FromPdfPage(page))
            {
                var opt = new XPdfFontOptions(PdfFontEncoding.Unicode);
                g.DrawString(pn == 0 ? LineArial : $"{LineArial} p{pn + 1}", new XFont("Arial", 14, XFontStyleEx.Regular, opt), XBrushes.Black, new XPoint(60, 100));
                g.DrawString(LineKorean, new XFont("Malgun Gothic", 14, XFontStyleEx.Regular, opt), XBrushes.Black, new XPoint(60, 140));
                using var img = XImage.FromStream(new MemoryStream(TestImages.Png(40, 30, (x, y) => ((byte)(x * 6), (byte)(y * 8), 200, 255))));
                g.DrawImage(img, 60, 200, 120, 90);
                g.DrawRectangle(new XPen(XColors.Red, 2), 260, 200, 150, 90);
                g.DrawLine(new XPen(XColors.Blue, 1.5), 60, 340, 400, 340);
            }
            AppendRawContent(doc, page, "/HelvF", "/Helvetica",
                $"BT /HelvF 12 Tf 1 0 0 1 60 420 Tm ({LineHelvetica}) Tj ET");
            }
            doc.Save(path);
        }
        if (stripToUnicode) StripToUnicode(path, "Malgun");
    }

    /// <summary>
    /// A3 가로 1쪽 도면형: 쪽 전체 테두리, 폼 XObject 안에 선·텍스트·폼(2단) 중첩, 클리핑, 점선, 반투명 채움, 그러데이션 채움.
    /// </summary>
    public static void CreateDrawingLike(string path) => CreateDrawingLike(path, true);

    public static void CreateDrawingLike(string path, bool gradient)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(1191);
        page.Height = XUnit.FromPoint(842);
        var opt = new XPdfFontOptions(PdfFontEncoding.Unicode);
        var font = new XFont("Arial", 10, XFontStyleEx.Regular, opt);

        // 안쪽 폼(2단)
        var inner = new XForm(doc, XUnit.FromPoint(200), XUnit.FromPoint(100));
        using (var gi = XGraphics.FromForm(inner))
        {
            gi.DrawRectangle(new XPen(XColors.Green, 1), 5, 5, 190, 90);
            gi.DrawString("INNER FORM TEXT", font, XBrushes.Black, new XPoint(15, 50));
        }
        // 바깥 폼
        var outer = new XForm(doc, XUnit.FromPoint(500), XUnit.FromPoint(300));
        using (var go = XGraphics.FromForm(outer))
        {
            go.DrawLine(new XPen(XColors.Black, 1), 0, 0, 500, 300);
            go.DrawString("OUTER FORM TEXT", font, XBrushes.Black, new XPoint(20, 30));
            go.DrawImage(inner, 250, 150);
        }

        using (var g = XGraphics.FromPdfPage(page))
        {
            g.DrawRectangle(new XPen(XColors.Black, 2), 20, 20, 1151, 802);             // 쪽 테두리
            g.DrawImage(outer, 60, 60);                                                 // 폼
            g.Save();
            g.IntersectClip(new XRect(650, 60, 200, 120));                              // 클리핑
            g.DrawEllipse(XBrushes.Orange, 600, 40, 300, 200);
            g.Restore();
            var dash = new XPen(XColors.DarkBlue, 1.5) { DashStyle = XDashStyle.Dash };
            g.DrawLine(dash, 60, 450, 1100, 450);                                       // 점선
            g.DrawRectangle(new XSolidBrush(XColor.FromArgb(128, 255, 0, 0)), 650, 250, 200, 120);   // 반투명
            if (gradient)   // 그러데이션 — 객체 편집 저장은 이런 쪽을 막는다(충실도 보호), 편집 시험 샘플은 끈다
                g.DrawRectangle(new XLinearGradientBrush(new XPoint(900, 250), new XPoint(1100, 370),
                    XColors.Yellow, XColors.Purple), 900, 250, 200, 120);
            g.DrawString("TITLE BLOCK 표제란", new XFont("Malgun Gothic", 12, XFontStyleEx.Regular, opt),
                XBrushes.Black, new XPoint(900, 780));
        }
        doc.Save(path);
    }

    /// <summary>
    /// A4 세로 1쪽, 선택 시험용(화면 위에서 아래 좌표 pt): 쪽의 약 89 %를 두른 속 빈 테두리(20,20,555,802), 안쪽 대각선 (100,100)-(300,300),
    /// 안쪽 속 빈 사각형 (350,100,150,150), 안쪽 칠한 사각형 (350,400,100,100).
    /// </summary>
    public static void CreateFrameWithInner(string path)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        using (var g = XGraphics.FromPdfPage(page))
        {
            g.DrawRectangle(new XPen(XColors.Black, 1), 20, 20, 555, 802);
            g.DrawLine(new XPen(XColors.Red, 1), 100, 100, 300, 300);
            g.DrawRectangle(new XPen(XColors.Blue, 1), 350, 100, 150, 150);
            g.DrawRectangle(new XSolidBrush(XColors.Green), 350, 400, 100, 100);
        }
        doc.Save(path);
    }

    /// <summary>A4 1쪽에 글 줄 count개("LINE n", 각각 텍스트 객체 하나)를 격자로 쓴다(큰 도면의 글 객체 수 시험).</summary>
    public static void CreateManyTextLines(string path, int count)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        using (var g = XGraphics.FromPdfPage(page))
        {
            var font = new XFont("Arial", 6, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
            for (int i = 0; i < count; i++) g.DrawString("LINE " + i, font, XBrushes.Black, new XPoint(20 + (i % 10) * 55, 20 + (i / 10) * 8));
        }
        doc.Save(path);
    }

    /// <summary>쪽 /Resources /Font에 비임베드 Type1 글꼴을 넣고 내용 스트림을 하나 덧붙인다.</summary>
    private static void AppendRawContent(PdfDocument doc, PdfPage page, string fontRes, string baseFont, string ops)
    {
        var f = new PdfDictionary(doc);
        f.Elements["/Type"] = new PdfName("/Font");
        f.Elements["/Subtype"] = new PdfName("/Type1");
        f.Elements["/BaseFont"] = new PdfName(baseFont);
        f.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        doc.Internals.AddObject(f);
        var res = page.Elements.GetDictionary("/Resources")!;
        var fonts = res.Elements.GetDictionary("/Font");
        if (fonts is null) { fonts = new PdfDictionary(doc); res.Elements["/Font"] = fonts; }
        fonts.Elements[fontRes] = f.Reference!;
        page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(ops));
    }

    /// <summary>BaseFont 이름에 marker가 든 Type0 글꼴의 /ToUnicode를 지운다.</summary>
    private static void StripToUnicode(string path, string marker)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        foreach (var o in doc.Internals.GetAllObjects())
            if (o is PdfDictionary d && d.Elements.GetName("/Subtype") == "/Type0"
                && (d.Elements.GetName("/BaseFont") ?? "").Contains(marker, StringComparison.OrdinalIgnoreCase))
                d.Elements.Remove("/ToUnicode");
        doc.Save(path);
    }
}
