using System.Text;
using System.Text.RegularExpressions;

namespace PdfEditor.TestFixtures;

/// <summary>Acrobat의 증분 저장을 흉내 내는 시험 도우미.</summary>
public static class PdfAppend
{
    /// <summary>끝에 어디서도 참조하지 않는 큰 스트림 객체와 새 xref·trailer를 덧붙인 PDF 바이트(PDFsharp이 쓴 고전 xref 파일 기준).</summary>
    public static byte[] UnreferencedObject(byte[] pdf, int junkBytes)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int sx = text.LastIndexOf("startxref", StringComparison.Ordinal);
        long prev = long.Parse(Regex.Match(text[(sx + 9)..], @"\d+").Value);
        string trailer = text[text.LastIndexOf("trailer", StringComparison.Ordinal)..];
        int size = int.Parse(Regex.Match(trailer, @"/Size\s+(\d+)").Groups[1].Value);
        string root = Regex.Match(trailer, @"/Root\s+(\d+ \d+ R)").Groups[1].Value;

        using var ms = new MemoryStream();
        ms.Write(pdf);
        long objOffset = ms.Length + 1;
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W($"\n{size} 0 obj\n<< /Length {junkBytes} >>\nstream\n");
        ms.Write(new byte[junkBytes].Select((_, i) => (byte)(i * 31 % 251)).ToArray());
        W("\nendstream\nendobj\n");
        long xrefOffset = ms.Length;
        W($"xref\n{size} 1\n{objOffset:D10} 00000 n \ntrailer\n<< /Size {size + 1} /Root {root} /Prev {prev} >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }
}
