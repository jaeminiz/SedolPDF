using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Optimize;

/// <summary>
/// 내용을 바꾸지 않는 정리(용량 줄이기 2단계). PDFsharp이 문서를 다시 쓸 때 카탈로그에서 닿는 객체만 쓰므로,
/// 증분 저장으로 쌓인 옛 객체와 어디서도 참조하지 않는 객체가 사라진다. 압축하지 않은 스트림은 Flate로 압축한다.
/// 쪽을 새 문서로 옮기지 않으므로 책갈피·양식·쪽 이름표 같은 문서 정보가 그대로 남는다.
/// </summary>
public static class LosslessCleaner
{
    public static byte[] Clean(byte[] pdf)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        doc.Options.CompressContentStreams = true;
        doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        using var output = new MemoryStream();
        doc.Save(output, closeStream: false);
        return output.ToArray();
    }
}
