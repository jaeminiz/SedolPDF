using Pdf.Native;

namespace Pdf.Native.Tests;

public class PdfiumThreadTests
{
    [Fact]
    public async Task Invoke_RunsOnSingleDedicatedThread()
    {
        using var t = new PdfiumThread();
        var ids = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => t.InvokeAsync(() => Environment.CurrentManagedThreadId))));
        Assert.Single(ids.Distinct());
        Assert.Equal(t.ManagedThreadId, ids[0]);
    }

    [Fact]
    public async Task Invoke_PreservesSubmissionOrder()
    {
        using var t = new PdfiumThread();
        var order = new List<int>();
        var tasks = Enumerable.Range(0, 100).Select(i => t.InvokeAsync(() => order.Add(i))).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(Enumerable.Range(0, 100), order);
    }

    [Fact]
    public async Task Invoke_ExceptionPropagatesToCaller()
    {
        using var t = new PdfiumThread();
        await Assert.ThrowsAsync<FormatException>(() =>
            t.InvokeAsync<int>(() => throw new FormatException("boom")));
        // 스레드는 예외 뒤에도 계속 동작해야 함
        Assert.Equal(7, await t.InvokeAsync(() => 7));
    }

    [Fact]
    public void AssertOnThread_FromOtherThread_Throws()
    {
        using var t = new PdfiumThread();
        Assert.Throws<InvalidOperationException>(() => t.AssertOnThread());
    }

    [Fact]
    public async Task AssertOnThread_FromDedicatedThread_Passes()
    {
        using var t = new PdfiumThread();
        await t.InvokeAsync(() => t.AssertOnThread());
    }

    [Fact]
    public async Task Invoke_Reentrant_RunsInlineWithoutDeadlock()
    {
        using var t = new PdfiumThread();
        var task = t.InvokeAsync(() => t.InvokeAsync(() => 42).GetAwaiter().GetResult());
        Assert.Equal(42, await task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Invoke_AfterDispose_ThrowsObjectDisposed()
    {
        var t = new PdfiumThread();
        t.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => t.InvokeAsync(() => 1));
    }

    [Fact]
    public async Task Dispose_WithPendingWork_CancelsPending()
    {
        var t = new PdfiumThread();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var running = t.InvokeAsync(() => { started.Set(); release.Wait(); });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        var pending = Enumerable.Range(0, 10).Select(_ => t.InvokeAsync(() => 1)).ToArray();

        var dispose = Task.Run(t.Dispose);
        await Task.Delay(100);
        release.Set();
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));

        await running;
        foreach (var p in pending)
            await Assert.ThrowsAnyAsync<TaskCanceledException>(() => p);
    }

    [Fact]
    public async Task InvokeHigh_RunsBeforeQueuedNormalWork()
    {
        using var t = new PdfiumThread();
        using var release = new ManualResetEventSlim();
        var order = new List<string>();
        var blocker = t.InvokeAsync(() => release.Wait());
        var normal = Enumerable.Range(0, 3).Select(i => t.InvokeAsync(() => order.Add("n" + i))).ToArray();
        var high = Enumerable.Range(0, 2).Select(i => t.InvokeHighAsync(() => { order.Add("h" + i); return i; })).ToArray();
        release.Set();
        await Task.WhenAll(normal.Concat<Task>(high).Append(blocker)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["h0", "h1", "n0", "n1", "n2"], order);
    }

    [Fact]
    public async Task Dispose_WithPendingHighWork_CancelsPending()
    {
        var t = new PdfiumThread();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var running = t.InvokeAsync(() => { started.Set(); release.Wait(); });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        var pending = t.InvokeHighAsync(() => 1);
        var dispose = Task.Run(t.Dispose);
        await Task.Delay(100);
        release.Set();
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        await running;
        await Assert.ThrowsAnyAsync<TaskCanceledException>(() => pending);
    }
}
