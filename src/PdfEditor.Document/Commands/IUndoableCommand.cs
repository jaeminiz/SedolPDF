namespace PdfEditor.Document.Commands;

/// <summary>
/// 실행 취소할 수 있는 명령. Execute·Undo는 모두 PDFium 전용 스레드에서 실행된다.
/// Execute가 예외를 던질 때는 문서를 실행 전 상태로 되돌려 둔 채 던져야 한다 (명령마다 내부에서 처리).
/// 명령이 IDisposable이면 스택에서 버려질 때 해제된다.
/// </summary>
public interface IUndoableCommand
{
    void Execute(DocumentSession s);
    void Undo(DocumentSession s);
}
