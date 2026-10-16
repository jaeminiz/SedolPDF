namespace Pdf.Native;

/// <summary>
/// PDFium 호출 전용 스레드 하나로 작업을 순서대로 실행하는 디스패처.
/// 대기열은 두 줄이다: 화면에 보이는 타일처럼 급한 작업(InvokeHighAsync)은 일반 작업보다 먼저 꺼낸다.
/// 같은 줄 안에서는 넣은 순서대로다. 실행 중인 작업을 끊지는 않는다.
/// </summary>
public sealed class PdfiumThread : IDisposable
{
    private sealed record WorkItem(Action Run, Action Cancel);

    private readonly Queue<WorkItem> _high = new();
    private readonly Queue<WorkItem> _normal = new();
    private readonly Thread _thread;
    private readonly object _gate = new();
    private bool _disposed;
    private int _disposedFlag;

    public PdfiumThread()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Pdfium" };
        _thread.Start();
    }

    public int ManagedThreadId => _thread.ManagedThreadId;

    private bool OnThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    public void AssertOnThread()
    {
        if (!OnThread)
            throw new InvalidOperationException("PDFium은 전용 스레드에서만 호출할 수 있습니다.");
    }

    public Task InvokeAsync(Action work, CancellationToken ct = default) =>
        InvokeAsync<object?>(() => { work(); return null; }, ct);

    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct = default) => Enqueue(work, ct, high: false);

    /// <summary>일반 대기열보다 먼저 실행한다(보이는 타일 렌더용). 짧은 작업에만 쓴다.</summary>
    public Task<T> InvokeHighAsync<T>(Func<T> work, CancellationToken ct = default) => Enqueue(work, ct, high: true);

    private Task<T> Enqueue<T>(Func<T> work, CancellationToken ct, bool high)
    {
        if (OnThread)
        {
            // 재진입: 큐에 넣으면 교착되므로 바로 실행
            try { return Task.FromResult(work()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(
            () =>
            {
                if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
                try { tcs.TrySetResult(work()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            },
            () => tcs.TrySetCanceled());

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            (high ? _high : _normal).Enqueue(item);
            Monitor.Pulse(_gate);
        }
        return tcs.Task;
    }

    private void Loop()
    {
        while (true)
        {
            WorkItem item;
            lock (_gate)
            {
                while (_high.Count == 0 && _normal.Count == 0 && !_disposed) Monitor.Wait(_gate);
                if (_high.Count > 0) item = _high.Dequeue();
                else if (_normal.Count > 0) item = _normal.Dequeue();
                else return;   // 닫혔고 남은 작업 없음
            }
            if (Volatile.Read(ref _disposedFlag) != 0) item.Cancel(); else item.Run();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Volatile.Write(ref _disposedFlag, 1);
            Monitor.PulseAll(_gate);
        }
        // 실행 중인 작업은 끝까지 돌고, 남은 작업은 Loop(또는 아래)가 Run하지 않고 취소한다
        if (!OnThread) _thread.Join();
        lock (_gate)
        {
            while (_high.TryDequeue(out var a)) a.Cancel();
            while (_normal.TryDequeue(out var b)) b.Cancel();
        }
    }
}
