using PixelRect = PdfEditor.App.Viewer.PixelRect;
using Avalonia;
using PdfEditor.App.Viewer;
using PdfEditor.Document;

namespace PdfEditor.App.Tests.Viewer;

public class TileSchedulerTests
{
    private static TileRequest R(int n, TileKind kind = TileKind.Tile, int x = 0, int y = 0) =>
        new(kind, new TileKey(new PageId(n), 1, 1000, x, y), 1.0, new PixelRect(x * 512, y * 512, 512, 512));

    private static async Task WaitFor(Func<bool> cond, int ms = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond()) { if (sw.ElapsedMilliseconds > ms) throw new TimeoutException(); await Task.Delay(5); }
    }

    [Fact]
    public async Task Scheduler_RespectsMaxInFlight()
    {
        int cur = 0, max = 0, done = 0;
        var s = new TileScheduler(async (r, ct) =>
        {
            var n = Interlocked.Increment(ref cur);
            lock (s_lock) max = Math.Max(max, n);
            await Task.Delay(20, CancellationToken.None);
            Interlocked.Decrement(ref cur);
            return new byte[1];
        }, (r, b) => Interlocked.Increment(ref done), 2);
        s.SetWanted(Enumerable.Range(0, 10).Select(i => R(i)).ToList());
        await WaitFor(() => Volatile.Read(ref done) == 10);
        Assert.True(max <= 2, $"max={max}");
        Assert.Equal(0, s.PendingCount);
        await WaitFor(() => s.InFlightCount == 0);
    }
    private static readonly object s_lock = new();

    [Fact]
    public async Task Scheduler_SetWanted_DropsQueuedNotInNewList()
    {
        var release = new TaskCompletionSource();
        var ready = new List<int>();
        var s = new TileScheduler(async (r, ct) =>
        {
            await release.Task;
            return new byte[1];
        }, (r, b) => { lock (ready) ready.Add((int)r.Key.Page.Value); }, 2);
        s.SetWanted(new[] { R(0), R(1), R(2), R(3) });
        await WaitFor(() => s.InFlightCount == 2);
        Assert.Equal(2, s.PendingCount);
        s.SetWanted(new[] { R(0), R(3) }); // 0 running (kept, not requeued), 1 finishes, 2 dropped
        Assert.Equal(1, s.PendingCount);   // only 3 queued
        release.SetResult();
        await WaitFor(() => { lock (ready) return ready.Count == 3; });
        Assert.DoesNotContain(2, ready);
        Assert.Contains(3, ready);
    }

    [Fact]
    public async Task Scheduler_PriorityOrder_VisibleBeforeRingBeforeThumb()
    {
        var vis = new[] { R(10, x: 2, y: 0), R(11, x: 0, y: 0), R(12, x: 1, y: 0) };
        var ring = new[] { R(20, x: 3, y: 0) };
        var thumbs = new[] { R(30, TileKind.Thumbnail) };
        var ordered = TilePriority.Order(vis, ring, thumbs, new Point(256, 256)); // nearest center = x0
        Assert.Equal(new long[] { 11, 12, 10, 20, 30 }, ordered.Select(r => r.Key.Page.Value));

        var order = new List<long>();
        var s = new TileScheduler((r, ct) => { lock (order) order.Add(r.Key.Page.Value); return Task.FromResult<byte[]?>(new byte[1]); },
            (r, b) => { }, 1);
        s.SetWanted(ordered);
        await WaitFor(() => { lock (order) return order.Count == 5; });
        Assert.Equal(new long[] { 11, 12, 10, 20, 30 }, order);
    }

    [Fact]
    public async Task Scheduler_StaleVersion_ResultDropped()
    {
        int ready = 0, calls = 0;
        var s = new TileScheduler((r, ct) => { Interlocked.Increment(ref calls); return Task.FromResult<byte[]?>(null); },
            (r, b) => Interlocked.Increment(ref ready));
        s.SetWanted(new[] { R(1), R(2) });
        await WaitFor(() => Volatile.Read(ref calls) == 2 && s.InFlightCount == 0);
        Assert.Equal(0, ready);
    }

    [Fact]
    public async Task Scheduler_RenderThrows_DoesNotStall()
    {
        int ready = 0;
        var s = new TileScheduler((r, ct) => r.Key.Page.Value == 0 ? throw new InvalidOperationException() : Task.FromResult<byte[]?>(new byte[1]),
            (r, b) => Interlocked.Increment(ref ready), 1);
        s.SetWanted(new[] { R(0), R(1) });
        await WaitFor(() => Volatile.Read(ref ready) == 1);
    }

    [Fact]
    public async Task Scheduler_Clear_DropsPendingAndCancelsInFlight()
    {
        int ready = 0; CancellationToken seen = default;
        var s = new TileScheduler(async (r, ct) =>
        {
            seen = ct;
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return null; }
            return new byte[1];
        }, (r, b) => Interlocked.Increment(ref ready), 1);
        s.SetWanted(new[] { R(0), R(1) });
        await WaitFor(() => s.InFlightCount == 1);
        s.Clear();
        Assert.Equal(0, s.PendingCount);
        await WaitFor(() => s.InFlightCount == 0);
        Assert.True(seen.IsCancellationRequested);
        Assert.Equal(0, ready);
    }
}
