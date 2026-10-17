namespace PdfEditor.Document.Commands;

/// <summary>
/// 여러 명령을 실행 취소 한 번으로 묶는다(예: 부모 주석과 그 답글·상태를 함께 지우기).
/// 순서대로 실행하다 중간에 실패하면 앞에서 실행한 것을 거꾸로 되돌린 뒤 예외를 다시 던진다. Undo는 거꾸로 되돌린다.
/// 조각은 주석 모델만 바꾸는 명령이어야 한다(CommandStack은 이 명령을 주석 명령으로 다룬다).
/// </summary>
public sealed class CompositeCommand(IReadOnlyList<IUndoableCommand> parts) : IAnnotationCommand, IDisposable
{
    private readonly IUndoableCommand[] _parts = [.. parts];

    public void Execute(DocumentSession s)
    {
        int done = 0;
        try
        {
            for (; done < _parts.Length; done++)
                _parts[done].Execute(s);
        }
        catch
        {
            // 실패한 조각은 스스로 원래대로 돌려 두고 던진다(IUndoableCommand 규약). 앞의 것만 되돌린다.
            for (int i = done - 1; i >= 0; i--)
            {
                try { _parts[i].Undo(s); }
                catch { /* 되돌리기 실패는 원래 예외를 가리지 않는다 */ }
            }
            throw;
        }
    }

    public void Undo(DocumentSession s)
    {
        for (int i = _parts.Length - 1; i >= 0; i--)
            _parts[i].Undo(s);
    }

    public void Dispose()
    {
        foreach (var p in _parts) (p as IDisposable)?.Dispose();
    }
}
