using Pdf.Native;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Document.Tests.Annotations;

public class ChangeAnnotationsCommandTests : IDisposable
{
    private readonly PdfiumThread _t = new();
    public void Dispose() => _t.Dispose();

    private static Annotation Sq(DocumentSession s) => new()
    {
        Id = s.Annotations.NewId(), Kind = AnnotKind.Square, Rect = new PdfRectF(10, 10, 50, 50),
    };

    [Fact]
    public async Task Execute_Undo_Redo_RestoresExactInstances()
    {
        await using var s = await DocumentSession.CreateNewAsync(_t);
        var page = s.Pages[0].Id;
        var a = Sq(s);
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(page, a));
        Assert.Same(a, s.Annotations.Get(page).Items[0].Data);

        var b = a with { BorderWidth = 4 };
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(page, [(a, b)]));
        Assert.Same(b, s.Annotations.Get(page).Items[0].Data);

        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Delete(page, [b]));
        Assert.Empty(s.Annotations.Get(page).Items);

        await s.Commands.UndoAsync();
        Assert.Same(b, s.Annotations.Get(page).Items[0].Data);
        await s.Commands.UndoAsync();
        Assert.Same(a, s.Annotations.Get(page).Items[0].Data);
        await s.Commands.UndoAsync();
        Assert.Empty(s.Annotations.Get(page).Items);
        await s.Commands.RedoAsync();
        Assert.Same(a, s.Annotations.Get(page).Items[0].Data);
    }

    [Fact]
    public async Task FailingChange_RevertsEarlierChangesInSameCommand()
    {
        await using var s = await DocumentSession.CreateNewAsync(_t);
        var page = s.Pages[0].Id;
        var a = Sq(s);
        var stale = Sq(s);   // 저장소에 없는 주석
        var cmd = new ChangeAnnotationsCommand(page,
        [
            new AnnotationChange(null, a),
            new AnnotationChange(stale, stale with { BorderWidth = 3 }),
        ]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Commands.ExecuteAsync(cmd));
        Assert.Empty(s.Annotations.Get(page).Items);
        Assert.False(s.Commands.CanUndo);
    }
}
