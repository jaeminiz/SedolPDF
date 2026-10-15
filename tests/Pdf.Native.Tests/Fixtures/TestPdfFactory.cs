using PdfSharp.Pdf;

namespace Pdf.Native.Tests.Fixtures;

/// <summary>테스트용 PDF 생성기 (PDFsharp 사용). 암호 PDF는 PDFium으로 만들 수 없어 여기서 만든다.</summary>
public static class TestPdfFactory
{
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

    private static PdfSharp.Pdf.PdfDocument Build(int pages, float w, float h)
    {
        var doc = new PdfSharp.Pdf.PdfDocument();
        for (int i = 0; i < pages; i++)
        {
            var p = doc.AddPage();
            p.Width = XUnitPt(w);
            p.Height = XUnitPt(h);
        }
        return doc;
    }

    private static PdfSharp.Drawing.XUnitPt XUnitPt(float v) => new(v);

    private static void Save(PdfSharp.Pdf.PdfDocument doc, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        doc.Save(path);
    }
}
