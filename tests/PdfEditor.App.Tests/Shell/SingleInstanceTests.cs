using PdfEditor.App;
using Xunit;

namespace PdfEditor.App.Tests.Shell;

public class SingleInstanceTests
{
    private static string NewName() => "SedolPDF-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Second_launch_is_not_primary_and_first_receives_the_paths()
    {
        var name = NewName();
        using var first = SingleInstance.TryBecomePrimary(name);
        Assert.NotNull(first);
        Assert.Null(await Task.Run(() => SingleInstance.TryBecomePrimary(name)));   // 뮤텍스는 같은 스레드에서 다시 잡히므로 다른 스레드로

        var got = new TaskCompletionSource<IReadOnlyList<string>>();
        first!.StartServer(p => got.TrySetResult(p));
        Assert.True(SingleInstance.TrySend(name, [@"C:\a b\one.pdf", @"C:\한글\둘.pdf"]));
        var r = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([@"C:\a b\one.pdf", @"C:\한글\둘.pdf"], r);
    }

    [Fact]
    public async Task Empty_message_only_activates()
    {
        var name = NewName();
        using var first = SingleInstance.TryBecomePrimary(name);
        var got = new TaskCompletionSource<IReadOnlyList<string>>();
        first!.StartServer(p => got.TrySetResult(p));
        Assert.True(SingleInstance.TrySend(name, []));
        var r = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(r);
    }

    [Fact]
    public async Task Server_keeps_accepting_after_a_message()
    {
        var name = NewName();
        using var first = SingleInstance.TryBecomePrimary(name);
        var all = new System.Collections.Concurrent.BlockingCollection<IReadOnlyList<string>>();
        first!.StartServer(all.Add);
        Assert.True(SingleInstance.TrySend(name, ["a"]));
        Assert.True(SingleInstance.TrySend(name, ["b"]));
        var ch = new[] { await Task.Run(() => all.Take(new CancellationTokenSource(5000).Token)), await Task.Run(() => all.Take(new CancellationTokenSource(5000).Token)) }; var (x, y) = (ch[0], ch[1]);
        Assert.Equal(["a", "b"], new[] { x[0], y[0] }.OrderBy(s => s));
    }

    [Fact]
    public void Send_fails_quickly_when_nobody_listens()
    {
        Assert.False(SingleInstance.TrySend(NewName(), ["x"], timeoutMs: 200));
    }

    [Fact]
    public void Mutex_is_released_on_dispose()
    {
        var name = NewName();
        SingleInstance.TryBecomePrimary(name)!.Dispose();
        using var again = SingleInstance.TryBecomePrimary(name);
        Assert.NotNull(again);
    }
}
