using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfEditor.ContentStream.Tests;

[Collection("RepairCache")]
public class ContentPrescanTests
{
    private static byte[] MakePdf(string tag)
    {
        using var doc = new PdfDocument();
        for (int i = 0; i < 3; i++)
        {
            var page = doc.AddPage();
            page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes($"q 0.{tag} 0 0 rg {i} 0 5 5 re f Q"));
        }
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    [Fact]
    public void Prescan_FillsCache_SoLaterRepairIsAHit()
    {
        var bytes = MakePdf("31");
        ContentPrescan.Run(bytes, CancellationToken.None);
        long h0 = ContentRepairCache.Hits;
        using var doc = PdfReader.Open(new MemoryStream(bytes), null, PdfDocumentOpenMode.Modify);
        for (int i = 0; i < doc.PageCount; i++)
            Assert.Null(ContentRepair.RepairStreamsCached(PageContent.LoadStreams(doc.Pages[i]).Streams!).Data);
        Assert.Equal(h0 + 3, ContentRepairCache.Hits);
    }

    [Fact]
    public void Prescan_Cancelled_DoesNotThrow()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        ContentPrescan.Run(MakePdf("32"), cts.Token);
        ContentPrescan.Run([1, 2, 3], CancellationToken.None);   // 열 수 없는 바이트
    }
}
