namespace PdfEditor.Document.Output;

/// <summary>저장 실패의 두 가지 뜻(0.15.0). LookChanged = 저장 결과는 만들 수 있지만 화면과 모양이 다를 수 있다("그래도 저장?"을 물을 수 있고 예면 반드시 저장된다). CannotRewrite = 이 편집은 이 쪽에서 저장할 수 없다(묻지 않고 원인 안내).</summary>
public enum RegenerationFailure { LookChanged, CannotRewrite }

/// <summary>
/// 객체 편집을 확정하려고 쪽 내용을 다시 썼더니 모양이 달라졌다(셰이딩·패턴 색·이미지 마스크 색 등 PDFium이 다시 쓰지 못하는 내용),
/// 그리고 내용 스트림 직접 고쳐 쓰기(M4x)도 할 수 없다(암호 문서·해석 불가·객체 번호 대응 실패 등 — <see cref="Reason"/>).
/// 망가진 파일을 내지 않으려고 저장·인쇄를 멈춘다. 사용자 문구는 Strings.Err_Save_ContentRegeneration / Err_Save_ObjectEditUnsupported(ErrorMessages).
/// <see cref="Kind"/>의 기본값은 CannotRewrite(패처가 던지는 고쳐 쓰기 불가) — 모양이 달라지는 경우만 LookChanged로 던진다.
/// </summary>
public sealed class ContentRegenerationException(int pageNumber, string? reason = null, RegenerationFailure kind = RegenerationFailure.CannotRewrite)
    : InvalidOperationException($"Rewriting the content of page {pageNumber} would change its look (shading, pattern or image-mask colors that PDFium cannot write back); save blocked." + (reason is null ? "" : " Content-stream rewrite: " + reason))
{
    public int PageNumber { get; } = pageNumber;
    public string? Reason { get; } = reason;
    public RegenerationFailure Kind { get; } = kind;
}
