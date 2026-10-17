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
