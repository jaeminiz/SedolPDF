namespace PdfEditor.Document.Commands;

/// <summary>실행 취소/다시 실행 스택. UI 스레드에서만 호출한다 (명령 자체는 PDFium 스레드에서 돈다).</summary>
public sealed class CommandStack
{
    private sealed record Entry(IUndoableCommand Command, long VersionBefore, long VersionAfter);

    private readonly DocumentSession _s;
    private readonly List<Entry> _undo = [];
    private readonly List<Entry> _redo = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _counter;
    private int _busy;

    internal CommandStack(DocumentSession s) => _s = s;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsBusy => Volatile.Read(ref _busy) > 0;

    /// <summary>실행·취소·재실행마다 바뀌는 상태 번호. 취소로 이전 상태에 돌아오면 그때의 번호로 복귀한다.</summary>
    public long Version { get; private set; }

    public event Action? Changed;

    public async Task ExecuteAsync(IUndoableCommand c)
    {
        await RunExclusive(async () =>
        {
            await _s.Thread.InvokeAsync(() => { Release(c); c.Execute(_s); _s.Publish(); }).ConfigureAwait(false);
            // 성공한 뒤에만 스택을 바꾼다 (실패하면 예외가 그대로 전달됨)
            DisposeAll(_redo);
            long before = Version;
            long after = ++_counter;
            _undo.Add(new Entry(c, before, after));
            Version = after;
        });
    }

    public async Task UndoAsync()
    {
        await RunExclusive(async () =>
        {
            if (_undo.Count == 0) return;
            var e = _undo[^1];
            await _s.Thread.InvokeAsync(() => { Release(e.Command); e.Command.Undo(_s); _s.Publish(); }).ConfigureAwait(false);
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(e);
            Version = e.VersionBefore;
        });
    }

    public async Task RedoAsync()
    {
        await RunExclusive(async () =>
        {
            if (_redo.Count == 0) return;
            var e = _redo[^1];
            await _s.Thread.InvokeAsync(() => { Release(e.Command); e.Command.Execute(_s); _s.Publish(); }).ConfigureAwait(false);
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(e);
            Version = e.VersionAfter;
        });
    }

    /// <summary>세션을 닫을 때. 남은 IDisposable 명령을 모두 해제한다.</summary>
    public void Clear()
    {
        DisposeAll(_undo);
        DisposeAll(_redo);
    }

    private async Task RunExclusive(Func<Task> body)
    {
        Interlocked.Increment(ref _busy);
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await body().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
            Changed?.Invoke();
        }
    }

    /// <summary>주석 명령은 쪽 구조를 바꾸지 않으므로 페이지 핸들을 유지한다 (전용 스레드에서 호출).</summary>
    private void Release(IUndoableCommand c)
    {
        if (c is not IAnnotationCommand) _s.ReleasePageHandles();
    }

    private static void DisposeAll(List<Entry> list)
    {
        foreach (var e in list) (e.Command as IDisposable)?.Dispose();
        list.Clear();
    }
}
