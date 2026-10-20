namespace PdfEditor.Document.Output;

/// <summary>
/// 객체 편집을 확정하려고 쪽 내용을 다시 썼더니 모양이 달라졌다(셰이딩·패턴 색·이미지 마스크 색 등 PDFium이 다시 쓰지 못하는 내용).
/// 망가진 파일을 내지 않으려고 저장·인쇄를 멈춘다. 사용자 문구는 Strings.Err_Save_ContentRegeneration(ErrorMessages).
/// </summary>
public sealed class ContentRegenerationException(int pageNumber)
    : InvalidOperationException($"Rewriting the content of page {pageNumber} would change its look (shading, pattern or image-mask colors that PDFium cannot write back); save blocked.")
{
    public int PageNumber { get; } = pageNumber;
}
