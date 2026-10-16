using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;

namespace PdfEditor.TestFixtures;

/// <summary>테스트용 PDF 생성기 (PDFsharp 사용). 암호 PDF 등은 PDFium으로 만들 수 없어 여기서 만든다.</summary>
public static class TestPdfFactory
{
    static TestPdfFactory()
    {
        // 브리프는 UseWindowsFontsUnderWindows = true 였으나, PDFsharp 내장 Windows 해석기는 맑은 고딕을
        // 찾지 못해(실측: "No appropriate font found") Arial·맑은 고딕을 C:\Windows\Fonts 에서 직접 읽는 해석기를 쓴다.
        GlobalFontSettings.FontResolver ??= new WindowsFontsResolver();
    }

    private sealed class WindowsFontsResolver : IFontResolver
    {
        private static readonly string Dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

        private static readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase)
        {
            ["arial|r"] = "arial.ttf", ["arial|b"] = "arialbd.ttf",
            ["arial|i"] = "ariali.ttf", ["arial|bi"] = "arialbi.ttf",
            ["malgun gothic|r"] = "malgun.ttf", ["malgun gothic|b"] = "malgunbd.ttf",
            ["malgun gothic|i"] = "malgun.ttf", ["malgun gothic|bi"] = "malgunbd.ttf",
        };

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            var key = $"{familyName.ToLowerInvariant()}|{(isBold ? "b" : "")}{(isItalic ? "i" : "")}";
            if (key.EndsWith('|')) key += "r";
            return Files.ContainsKey(key) ? new FontResolverInfo(key) : null;
        }

        public byte[]? GetFont(string faceName) => File.ReadAllBytes(Path.Combine(Dir, Files[faceName]));
    }

    public static void CreateBlank(string path, int pages, float widthPt, float heightPt)
    {
        using var doc = Build(pages, widthPt, heightPt);
        Save(doc, path);
    }

    public static void CreateEncrypted(string path, int pages, float widthPt, float heightPt, string userPassword)
    {
        using var doc = Build(pages, widthPt, heightPt);
        doc.SecuritySettings.UserPassword = userPassword;
        doc.SecuritySettings.OwnerPassword = userPassword + "-owner";
        Save(doc, path);
    }

    /// <summary>쪽마다 왼쪽 위에 "PAGE n"과 20×20pt 검정 표식을 그린다.</summary>
    public static void CreateLabeled(string path, IReadOnlyList<(float W, float H)> sizes)
    {
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var font = new XFont("Arial", 12);
        for (int i = 0; i < sizes.Count; i++)
        {
            var p = doc.AddPage();
            p.Width = new XUnitPt(sizes[i].W);
            p.Height = new XUnitPt(sizes[i].H);
            using var gfx = XGraphics.FromPdfPage(p);
            gfx.DrawRectangle(XBrushes.Black, new XRect(0, 0, 20, 20));
            gfx.DrawString($"PAGE {i + 1}", font, XBrushes.Black, new XPoint(26, 15));
        }
        Save(doc, path);
    }

    /// <summary>맑은 고딕으로 한 줄 한글 텍스트. 테스트 실행 중에만 생성하고 커밋하지 않는다.</summary>
    public static void CreateKoreanText(string path, string text)
    {
        using var doc = Build(1, 595, 842);
        using (var gfx = XGraphics.FromPdfPage(doc.Pages[0]))
        {
            var opts = new XPdfFontOptions(PdfFontEncoding.Unicode);
            XFont font;
            try { font = new XFont("Malgun Gothic", 14, XFontStyleEx.Regular, opts); }
            catch (InvalidOperationException)
            {
                // PDFsharp 글꼴 해석기가 한국어 로캘에서 등록된 이름("맑은 고딕")으로만 찾는 경우 — 검증 필요
                font = new XFont("맑은 고딕", 14, XFontStyleEx.Regular, opts);
            }
            gfx.DrawString(text, font, XBrushes.Black, new XPoint(50, 80));
        }
        Save(doc, path);
    }

    /// <summary>3쪽. 책갈피: 1장(→1쪽) 아래 1.1 절(→2쪽), 2장(→3쪽, 닫힘).</summary>
    public static void CreateWithOutline(string path)
    {
        using var doc = Build(3, 595, 842);
        var ch1 = doc.Outlines.Add("1장", doc.Pages[0], true);
        ch1.Outlines.Add("1.1 절", doc.Pages[1], true);
        doc.Outlines.Add("2장", doc.Pages[2], false);
        Save(doc, path);
    }

    /// <summary>/Names /EmbeddedFiles 이름 트리를 저수준 객체로 직접 만든다 (PDFsharp에 첨부 API 없음 — 검증 필요).</summary>
    public static void CreateWithAttachment(string path, string name, byte[] data)
    {
        using var doc = Build(1, 595, 842);

        var ef = new PdfDictionary(doc);
        ef.Elements["/Type"] = new PdfName("/EmbeddedFile");
        ef.CreateStream(data);
        doc.Internals.AddObject(ef);

        var efDict = new PdfDictionary(doc);
        efDict.Elements["/F"] = ef.Reference;

        var spec = new PdfDictionary(doc);
        spec.Elements["/Type"] = new PdfName("/Filespec");
        spec.Elements["/F"] = new PdfString(name);
        spec.Elements["/UF"] = new PdfString(name);
        spec.Elements["/EF"] = efDict;
        doc.Internals.AddObject(spec);

        var tree = new PdfDictionary(doc);
        tree.Elements["/Names"] = new PdfArray(doc, new PdfString(name), spec.Reference!);
        var names = new PdfDictionary(doc);
        names.Elements["/EmbeddedFiles"] = tree;
        doc.Internals.Catalog.Elements["/Names"] = names;
        Save(doc, path);
    }

    /// <summary>레이어(OCG)마다 내용(색 사각형)을 /OC BDC … EMC 로 그린다. 쪽은 1개.</summary>
    public static void CreateWithLayers(string path, IReadOnlyList<string> layerNames)
    {
        using var doc = Build(1, 595, 842);
        var page = doc.Pages[0];

        var ocgRefs = new List<PdfItem>();
        var props = new PdfDictionary(doc);
        var content = new StringBuilder();
        for (int i = 0; i < layerNames.Count; i++)
        {
            var ocg = new PdfDictionary(doc);
            ocg.Elements["/Type"] = new PdfName("/OCG");
            ocg.Elements["/Name"] = new PdfString(layerNames[i]);
            doc.Internals.AddObject(ocg);
            ocgRefs.Add(ocg.Reference!);
            props.Elements[$"/Lyr{i}"] = ocg.Reference!;
            content.Append($"/OC /Lyr{i} BDC 0 0 1 rg {50 + i * 60} 700 50 50 re f EMC\n");
        }

        page.Resources.Elements["/Properties"] = props;
        var stream = page.Contents.AppendContent();
        stream.CreateStream(Encoding.ASCII.GetBytes(content.ToString()));

        var d = new PdfDictionary(doc);
        d.Elements["/Order"] = new PdfArray(doc, ocgRefs.ToArray());
        d.Elements["/ON"] = new PdfArray(doc, ocgRefs.ToArray());
        var ocProps = new PdfDictionary(doc);
        ocProps.Elements["/OCGs"] = new PdfArray(doc, ocgRefs.ToArray());
        ocProps.Elements["/D"] = d;
        doc.Internals.Catalog.Elements["/OCProperties"] = ocProps;
        Save(doc, path);
    }

    /// <summary>MediaBox는 A4(595.28×841.89), CropBox는 인자 (왼쪽·아래·오른쪽·위).</summary>
    public static void CreateWithCropBox(string path, (float L, float B, float R, float T) crop)
    {
        using var doc = Build(1, 595.28f, 841.89f);
        var page = doc.Pages[0];
        page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(595.28, 841.89));
        page.CropBox = new PdfRectangle(new XPoint(crop.L, crop.B), new XPoint(crop.R, crop.T));
        Save(doc, path);
    }

    /// <summary>열기 암호 없음, 권한(소유자) 암호만. 허용 여부는 인쇄·수정·복사.</summary>
    public static void CreateOwnerRestricted(string path, bool allowPrint, bool allowModify, bool allowCopy)
    {
        using var doc = Build(1, 595, 842);
        var s = doc.SecuritySettings;
        s.OwnerPassword = "owner-secret";
        s.PermitPrint = allowPrint;
        s.PermitFullQualityPrint = allowPrint;
        s.PermitModifyDocument = allowModify;
        s.PermitAssembleDocument = allowModify;
        s.PermitAnnotations = allowModify;
        s.PermitFormsFill = allowModify;
        s.PermitExtractContent = allowCopy;
        Save(doc, path);
    }

    /// <summary>기존 파일의 pageIndex 쪽에 스티커 메모 1개를 추가한다.</summary>
    public static void AddTextAnnotation(string path, int pageIndex, bool printFlag = false)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        var annot = new PdfTextAnnotation
        {
            Contents = "test note",
            Icon = PdfTextAnnotationIcon.Note,
            Rectangle = new PdfRectangle(new XRect(100, 100, 24, 24)),
        };
        // 주석 플래그의 Print 비트(4)가 없으면 인쇄(FPDF_PRINTING) 때 PDFium이 그리지 않는다.
        if (printFlag) annot.Flags |= PdfAnnotationFlags.Print;
        doc.Pages[pageIndex].Annotations.Add(annot);
        doc.Save(path);
    }

    /// <summary>마지막 startxref 뒤 숫자를 같은 자릿수의 틀린 값(9…9)으로 덮어써 PDFium이 복구해서 열게 한다.</summary>
    public static void CorruptXref(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var key = Encoding.ASCII.GetBytes("startxref");
        int pos = bytes.AsSpan().LastIndexOf(key);
        if (pos < 0) throw new InvalidOperationException("startxref not found");
        int i = pos + key.Length;
        while (i < bytes.Length && (bytes[i] == (byte)'\r' || bytes[i] == (byte)'\n' || bytes[i] == (byte)' ')) i++;
        for (; i < bytes.Length && bytes[i] >= (byte)'0' && bytes[i] <= (byte)'9'; i++) bytes[i] = (byte)'9';
        File.WriteAllBytes(path, bytes);
    }

    private static PdfSharp.Pdf.PdfDocument Build(int pages, float w, float h)
    {
        var doc = new PdfSharp.Pdf.PdfDocument();
        for (int i = 0; i < pages; i++)
        {
            var p = doc.AddPage();
            p.Width = new XUnitPt(w);
            p.Height = new XUnitPt(h);
        }
        return doc;
    }

    private static void Save(PdfSharp.Pdf.PdfDocument doc, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        doc.Save(path);
    }
}
