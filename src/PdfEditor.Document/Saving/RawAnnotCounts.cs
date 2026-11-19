using PdfSharp.Pdf.IO;

namespace PdfEditor.Document.Saving;

/// <summary>
/// 0.14.0: 쪽마다 /Annots 배열의 원시 길이. PDFium으로 쪽을 불러(내용 해석) 주석 수를 세는 것보다 훨씬 싸다(75쪽 도면 0.8초 → 수십 ms).
/// 같은 파일 상태끼리만 비교한다(PDFium의 주석 수와는 Popup 등에서 다를 수 있다).
/// </summary>
internal static class RawAnnotCounts
{
    /// <summary>읽지 못하면 null(호출자는 예전처럼 PDFium으로 센다).</summary>
    public static int[]? TryRead(string path, string? password)
    {
        try
        {
            using var doc = PdfReader.Open(new MemoryStream(File.ReadAllBytes(path)), password, PdfDocumentOpenMode.Import);
            var r = new int[doc.PageCount];
            for (int i = 0; i < r.Length; i++) r[i] = doc.Pages[i].Elements.GetArray("/Annots")?.Elements.Count ?? 0;
            return r;
        }
        catch (Exception) { return null; }
    }
}
