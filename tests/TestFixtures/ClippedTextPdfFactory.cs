using System.Text;
using PdfSharp.Pdf;

namespace PdfEditor.TestFixtures;

/// <summary>
/// 0.10.0 Task 0 시험용: Chromium처럼 글 덩어리마다 `re W n`으로 잘림 상자를 둘러 놓은 PDF.
/// 잘린 글(y=100)과 같은 글의 잘림 없는 쌍둥이(y=400)가 한 쪽에 있다.
/// </summary>
public static class ClippedTextPdfFactory
{
    public const string Text = "Hello clipped world";

    public static void Create(string path)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = PdfSharp.Drawing.XUnit.FromPoint(595);
        page.Height = PdfSharp.Drawing.XUnit.FromPoint(842);
        var f = new PdfDictionary(doc);
        f.Elements["/Type"] = new PdfName("/Font");
        f.Elements["/Subtype"] = new PdfName("/Type1");
        f.Elements["/BaseFont"] = new PdfName("/Helvetica");
        f.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        doc.Internals.AddObject(f);
        var res = new PdfDictionary(doc);
        var fonts = new PdfDictionary(doc);
        fonts.Elements["/F1"] = f.Reference!;
        res.Elements["/Font"] = fonts;
        page.Elements["/Resources"] = res;
        string ops =
            $"q 100 100 70 20 re W n BT /F1 14 Tf 102 104 Td ({Text}) Tj ET Q\n" +
            $"q BT /F1 14 Tf 102 400 Td ({Text}) Tj ET Q\n";
        page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(ops));
        doc.Save(path);
    }
}
