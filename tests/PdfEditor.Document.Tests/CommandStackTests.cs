using PdfEditor.Document.Commands;

namespace PdfEditor.Document.Tests;

public class CommandStackTests : IDisposable
{
    private readonly Pdf.Native.PdfiumThread _t = new();

    public void Dispose() => _t.Dispose();

    private sealed class Counter { public int Value; }

    private sealed class FakeCommand(Counter c) : IUndoableCommand, IDisposable
    {
        public bool Disposed;
        public int? ThreadId;
        public void Execute(DocumentSession s) { ThreadId = Environment.CurrentManagedThreadId; c.Value++; }
        public void Undo(DocumentSession s) { c.Value--; }
        public void Dispose() => Disposed = true;
    }

    private sealed class FailingCommand : IUndoableCommand
    {
        public void Execute(DocumentSession s) => throw new InvalidOperationException("boom");
        public void Undo(DocumentSession s) { }
    }

    [Fact]
    public async Task Execute_Undo_Redo_UpdatesState()
    {
        await using var s = await DocumentSession.CreateNewAsync(_t);
        var c = new Counter();
        var changed = 0;
        s.Commands.Changed += () => changed++;
        long v0 = s.Commands.Version;
        Assert.False(s.Commands.CanUndo);
        Assert.False(s.Commands.CanRedo);

        await s.Commands.ExecuteAsync(new FakeCommand(c));
        Assert.Equal(1, c.Value);
        Assert.True(s.Commands.CanUndo);
        Assert.False(s.Commands.CanRedo);
        long v1 = s.Commands.Version;
        Assert.NotEqual(v0, v1);

        await s.Commands.UndoAsync();
        Assert.Equal(0, c.Value);
        Assert.False(s.Commands.CanUndo);
        Assert.True(s.Commands.CanRedo);
        Assert.Equal(v0, s.Commands.Version);

        await s.Commands.RedoAsync();
        Assert.Equal(1, c.Value);
        Assert.Equal(v1, s.Commands.Version);
        Assert.True(changed >= 3);
        Assert.False(s.Commands.IsBusy);
    }

    [Fact]
    public async Task UndoToSavedPoint_NotDirty()
    {
        await using var s = await DocumentSession.CreateNewAsync(_t);
        var c = new Counter();
        await s.Commands.ExecuteAsync(new FakeCommand(c));
        Assert.True(s.IsDirty);
        s.MarkSaved(Path.Combine(Path.GetTempPath(), "x.pdf"));
        Assert.False(s.IsDirty);
        Assert.Equal("x.pdf", s.DisplayName);
        await s.Commands.ExecuteAsync(new FakeCommand(c));
        Assert.True(s.IsDirty);
        await s.Commands.UndoAsync();
        Assert.False(s.IsDirty);
        await s.Commands.UndoAsync();
        Assert.True(s.IsDirty);
    }

    [Fact]
    public async Task FailingCommand_NotPushed_ExceptionPropagates()
    {
        await using var s = await DocumentSession.CreateNewAsync(_t);
        long v = s.Commands.Version;
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Commands.ExecuteAsync(new FailingCommand()));
        Assert.False(s.Commands.CanUndo);
        Assert.Equal(v, s.Commands.Version);
        Assert.False(s.Commands.IsBusy);
        // 실패 뒤에도 계속 쓸 수 있다
        await s.Commands.ExecuteAsync(new FakeCommand(new Counter()));
        Assert.True(s.Commands.CanUndo);
    }

    [Fact]
    public async Task NewCommandAfterUndo_DisposesRedoCommands()
    {
        await using var s = await DocumentSession.CreateNewAsync(_t);
        var c = new Counter();
        var first = new FakeCommand(c);
        await s.Commands.ExecuteAsync(first);
        await s.Commands.UndoAsync();
        Assert.False(first.Disposed);
        await s.Commands.ExecuteAsync(new FakeCommand(c));
        Assert.True(first.Disposed);
    }

    [Fact]
    public async Task SessionDispose_ClearsAndDisposesCommands()
    {
        var s = await DocumentSession.CreateNewAsync(_t);
        var cmd = new FakeCommand(new Counter());
        await s.Commands.ExecuteAsync(cmd);
        await s.DisposeAsync();
        Assert.True(cmd.Disposed);
    }

    private sealed class NoopAnnotationCommand : IAnnotationCommand
    {
        public void Execute(DocumentSession s) { }
        public void Undo(DocumentSession s) { }
    }

    [Fact]
    public async Task AnnotationCommand_DoesNotReleasePageHandles()
    {
        await using var s = await DocumentSession.CreateNewAsync(_t);
        var id = s.Pages[0].Id;
        var before = await _t.InvokeAsync(() => s.GetLoadedPage(id));
        await s.Commands.ExecuteAsync(new NoopAnnotationCommand());
        Assert.Same(before, await _t.InvokeAsync(() => s.GetLoadedPage(id)));
        await s.Commands.UndoAsync();
        Assert.Same(before, await _t.InvokeAsync(() => s.GetLoadedPage(id)));
        await s.Commands.RedoAsync();
        Assert.Same(before, await _t.InvokeAsync(() => s.GetLoadedPage(id)));
        // 일반 명령은 여전히 핸들을 비운다
        await s.Commands.ExecuteAsync(new FakeCommand(new Counter()));
        Assert.NotSame(before, await _t.InvokeAsync(() => s.GetLoadedPage(id)));
    }

    [Fact]
    public async Task Execute_RunsOnPdfiumThread()
    {
        await using var s = await DocumentSession.CreateNewAsync(_t);
        var cmd = new FakeCommand(new Counter());
        await s.Commands.ExecuteAsync(cmd);
        Assert.Equal(_t.ManagedThreadId, cmd.ThreadId);
    }
}
