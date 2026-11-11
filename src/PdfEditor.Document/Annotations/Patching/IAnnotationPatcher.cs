namespace PdfEditor.Document.Annotations.Patching;

/// <summary>SaveCopy로 만든 임시 파일에 계획을 적용한다(PDFsharp). 편집 중인 PDFium 문서는 건드리지 않는다.</summary>
public interface IAnnotationPatcher
{
    /// <summary>
    /// 순서 고정: ① Patches(번호가 바뀌기 전) ② Creates(끝에 붙임), 그다음 EnsurePopup의 Popup도 끝에
    /// ③ Removals(쪽마다 큰 번호부터). 적용 뒤 같은 경로에 저장한다.
    /// </summary>
    void Apply(string pdfPath, string? password, PatchPlan plan, AnnotationAssets assets);
}

/// <summary>
/// 선택 기능(2026-11-10): 저장한 파일의 쪽 내용을 고친다(ContentRepair). 짝 없는 Q는 Acrobat이 쪽 그리기를 멈추게 하고, 꼬리 조각은 쪽 일부를 뒤집어 그리게 한다(0.10.0).
/// 0.8.0까지의 저장이 만든 파일을 다시 저장할 때 고치려는 것. 고친 쪽이 있을 때만 같은 경로에 다시 저장하고, 고친 쪽 번호를 돌려준다.
/// </summary>
public interface IContentRepairer
{
    /// <summary>고친 쪽의 번호(0부터). 고친 쪽이 없으면 빈 목록이고 파일은 그대로다.</summary>
    IReadOnlyList<int> RepairContent(string pdfPath, string? password);
}

/// <summary>선택 기능(M5 C12): 가장 최근 <see cref="IAnnotationPatcher.Apply"/>에서 지우지 못하고 숨김으로 남긴 원본 글 객체 수.</summary>
public interface IContentRewriteReport
{
    int HiddenTextCount { get; }
}

/// <summary>
/// 선택 기능(M5 C7): 패처가 어떤 쪽의 내용 스트림 직접 고쳐 쓰기를 실제로 할 수 있는지 파일을 바꾸지 않고 미리 해 본다.
/// 문단 편집이 있는 쪽은 이 방식을 먼저 쓰고, 거절되면(객체 번호 대응·글꼴·상태 문제) PDFium 경로로 내려가기 때문이다.
/// </summary>
public interface IContentRewriteProbe
{
    /// <summary>못 하는 쪽 번호(0부터) → 이유. 모두 되면 빈 표. 파일은 읽기만 한다.</summary>
    IReadOnlyDictionary<int, string> CheckContentRewrites(string pdfPath, string? password, IReadOnlyList<PageContentRewrite> pages, AnnotationAssets assets);
}
