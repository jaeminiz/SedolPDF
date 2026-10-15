using System.Collections.Concurrent;

namespace Pdf.Native;

/// <summary>PDFium 호출 전용 스레드 하나로 작업을 순서대로 실행하는 디스패처.</summary>
public sealed class PdfiumThread : IDisposable
{
    private sealed record WorkItem(Action Run, Action Cancel);

    private readonly BlockingCollection<WorkItem> _queue = new();
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

    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct = default)
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
            _queue.Add(item);
        }
        return tcs.Task;
    }

    private void Loop()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
            if (Volatile.Read(ref _disposedFlag) != 0) item.Cancel(); else item.Run();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Volatile.Write(ref _disposedFlag, 1);
            _queue.CompleteAdding();
        }
        // 실행 중인 작업이 끝난 뒤 남은 작업은 Loop가 Run하지 않도록 아래에서 취소 처리
        if (!OnThread) _thread.Join();
        while (_queue.TryTake(out var left)) left.Cancel();
        _queue.Dispose();
    }
}
