namespace PdfEditor.Document.Saving;

/// <summary>임시 파일에 PDFium 저장이 끝난 뒤 실행되는 후처리 (M2의 PDFsharp 주석 후처리가 들어옴).</summary>
public interface ISavePostProcessor
{
    bool AppliesTo(DocumentSession s);
    void Process(string tempPath, string? password);
}
