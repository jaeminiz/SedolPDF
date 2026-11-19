using System.Text;
using Pdf.Native;
using PdfSharp.Pdf.IO;
using NativeDoc = Pdf.Native.PdfDocument;

namespace PdfEditor.Appearance.Tests;

/// <summary>
/// 0.14.0 작업 6(안 C 사전 확인): PDFium의 쪽별 주석 수와 원시 /Annots 길이가 같은가. 실제 PDF(private/ + SEDOL_BENCH_PDF)의 결과를 %TEMP%\sedol-annot-count-check.txt에 적는다.
/// 구현은 두 방법을 섞어 비교하지 않으므로(열 때 PDFium으로 센 값은 PDFium끼리, 원시 길이는 원시끼리) 달라도 저장은 거짓 실패하지 않는다 — 다른 쪽을 보고하는 용도다.
/// </summary>
public class AnnotCountMethodsTests
{
    [Fact]
    public async Task PdfiumCount_VersusRawAnnots_Report()
    {
        var sb = new StringBuilder();
        int pages = 0, diff = 0;
        using var t = new PdfiumThread();
        foreach (var path in ContentRepairEquivalenceTests.RealPdfs())
        {
            int[] raw;
            try
            {
                using var d = PdfReader.Open(new MemoryStream(File.ReadAllBytes(path)), null, PdfDocumentOpenMode.Import);
                raw = Enumerable.Range(0, d.PageCount).Select(i => d.Pages[i].Elements.GetArray("/Annots")?.Elements.Count ?? 0).ToArray();
            }
            catch { continue; }
            int[] counts = await t.InvokeAsync(() =>
            {
                using var doc = NativeDoc.Open(t, path, null);
                var r = new int[doc.PageCount];
                for (int i = 0; i < r.Length; i++) { using var p = doc.LoadPage(i); r[i] = p.AnnotationCount; }
                return r;
            });
            for (int i = 0; i < Math.Min(raw.Length, counts.Length); i++)
            {
                pages++;
                if (raw[i] != counts[i]) { diff++; sb.AppendLine($"{Path.GetFileName(path)} p{i + 1}: pdfium={counts[i]} raw={raw[i]}"); }
            }
        }
        sb.Insert(0, $"pages={pages} different={diff}\n");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "sedol-annot-count-check.txt"), sb.ToString());
        Assert.True(pages >= 0);
    }
}
