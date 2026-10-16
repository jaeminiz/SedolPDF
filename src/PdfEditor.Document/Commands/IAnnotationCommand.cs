namespace PdfEditor.Document.Commands;

/// <summary>
/// 주석 모델(AnnotationStore)만 바꾸는 명령. PDFium 문서와 쪽 구조를 건드리지 않으므로
/// CommandStack이 실행 전후에 페이지 핸들을 비우지 않는다 (큰 도면의 쪽 다시 불러오기를 피함).
/// </summary>
public interface IAnnotationCommand : IUndoableCommand
{
}
