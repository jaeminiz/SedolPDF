using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;

namespace PdfEditor.Document.Tests.Content;

/// <summary>M5 Task 5: ReplaceParagraphEdit의 기록·replay(화면/출력)·명령·쪽 작업과의 상호작용. 편집은 손으로 짠다(계획은 TextEdit 시험).</summary>
public sealed class ParagraphReplayTests : IDisposable
{
    private const string NewText = "Arial 321 CBA";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-m5replay-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public ParagraphReplayTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<DocumentSession> Open(int pages = 1)
    {
        string p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        ContentPdfFactory.CreateDocumentLike(p, pages: pages);
        return await DocumentSession.OpenAsync(_t, p, null);
    }

    private Task<T> On<T>(Func<T> f) => _t.InvokeAsync(f);

    private static int IndexOfText(DocumentSession s, PageId id, string startsWith) =>
        s.GetObjectIndex(id).Entries.First(e => e.Type == PdfPageObjectType.Text && (e.TextPreview ?? "").StartsWith(startsWith)).Ref.OriginalIndex;

    private static string PageText(DocumentSession s, PageId id)
    {
        var tp = s.GetLoadedText(id);
        return tp.GetText(0, tp.CharCount);
    }

    /// <summary>Arial 줄을 끄고 원래 글꼴 새 줄 하나를 같은 자리(+dx)에 넣는 문단 편집.</summary>
    private static ReplaceParagraphEdit Edit(DocumentSession s, PageId id, string text = NewText, float dx = 0, int arial = -1)
    {
        if (arial < 0) arial = IndexOfText(s, id, "Arial");
        var obj = s.GetReplay(id).Resolve(ObjectRef.Original(arial))!;
        var m = obj.Matrix;
        var line = new ParagraphTextLine(s.Content.NewId(), text, new PdfMatrix(m.A, m.B, m.C, m.D, m.E + dx, m.F), obj.FontSize, new PdfColor(0, 0, 0),
            ObjectRef.Original(arial), 0, 100, 0, obj.TextRenderMode);
        var meta = new ParagraphMeta(obj.Bounds, "Arial", text, "Left", "Arial|True", 12, null);
        return new ReplaceParagraphEdit(s.Content.NewId(), [ObjectRef.Original(arial)], [line], [], [], meta);
    }

    [Fact]
    public async Task Replay_RemovedLinesInactive_NewLineInserted_UnchangedLinesUntouched()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        var e = await On(() => Edit(s, id));
        int before = await On(() => s.GetObjectIndex(id).Entries.Count(x => x.Type == PdfPageObjectType.Text));
        await s.Commands.ExecuteAsync(new ParagraphEditCommand(id, null, e));
        await On(() =>
        {
            var r = s.GetReplay(id);
            Assert.Equal(0, r.Skipped);
            Assert.False(r.Resolve(e.Remove[0])!.IsActive);
            Assert.NotNull(r.Resolve(ObjectRef.Inserted(e.OriginalFontLines[0].LineId)));
            Assert.Equal(before, s.GetObjectIndex(id).Entries.Count(x => x.Type == PdfPageObjectType.Text));   // 하나 끄고 하나 넣음 — 나머지 줄은 그대로
            string text = PageText(s, id);
            Assert.Contains(NewText, text);
            Assert.DoesNotContain(ContentPdfFactory.LineArial, text);
            return 0;
        });
    }

    [Fact]
    public async Task Replay_NewLine_UsesPlacementAndKeepsDrawingOrder()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        var e = await On(() => Edit(s, id, dx: 30));
        await s.Commands.ExecuteAsync(new ParagraphEditCommand(id, null, e));
        await On(() =>
        {
            var r = s.GetReplay(id);
            var o = r.Resolve(ObjectRef.Inserted(e.OriginalFontLines[0].LineId))!;
            Assert.Equal(e.OriginalFontLines[0].Placement.E, o.Matrix.E, 2);
            // 원본 줄 자리(그리기 순서) 바로 뒤에 들어간다: 앞쪽 원본 객체는 모두 아래, 뒤쪽 원본 객체는 모두 위
            var idx = s.GetObjectIndex(id);
            int newOrder = idx.Entries.Single(x => x.Ref == ObjectRef.Inserted(e.OriginalFontLines[0].LineId)).Order;
            Assert.All(idx.Entries.Where(x => !x.Ref.IsInserted && x.Ref.OriginalIndex < e.Remove[0].OriginalIndex), x => Assert.True(x.Order < newOrder));
            Assert.All(idx.Entries.Where(x => !x.Ref.IsInserted && x.Ref.OriginalIndex > e.Remove[0].OriginalIndex), x => Assert.True(x.Order > newOrder));
            return 0;
        });
    }

    [Fact]
    public async Task OutputReplay_OnlyDeactivates_NoNewLines()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        var e = await On(() => Edit(s, id));
        await On(() =>
        {
            using var page = s.Document.LoadPage(0);
            var r = ContentReplayer.Apply(page, [e], ReplayMode.Output);
            Assert.Empty(r.Inserted);
            Assert.False(r.Resolve(e.Remove[0])!.IsActive);
            Assert.Equal(0, r.Skipped);
            return 0;
        });
    }

    [Fact]
    public async Task Undo_RestoresOriginalLines_Redo_SameResult()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        string before = await On(() => PageText(s, id));
        var e = await On(() => Edit(s, id));
        await s.Commands.ExecuteAsync(new ParagraphEditCommand(id, null, e));
        string edited = await On(() => PageText(s, id));
        Assert.NotEqual(before, edited);
        await s.Commands.UndoAsync();
        Assert.Equal(before, await On(() => PageText(s, id)));
        Assert.False(s.Content.HasEdits);
        await s.Commands.RedoAsync();
        Assert.Equal(edited, await On(() => PageText(s, id)));
    }

    [Fact]
    public async Task ReEdit_ReplacesPreviousEdit_NoStacking_AndRevertRemovesIt()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        int ar = await On(() => IndexOfText(s, id, "Arial"));
        var first = await On(() => Edit(s, id, "Arial 111", arial: ar));
        await s.Commands.ExecuteAsync(new ParagraphEditCommand(id, null, first));
        var second = await On(() => Edit(s, id, "Arial 222", arial: ar));
        await s.Commands.ExecuteAsync(new ParagraphEditCommand(id, first, second));
        Assert.Single(s.Content.For(id).OfType<ReplaceParagraphEdit>());
        string t = await On(() => PageText(s, id));
        Assert.Contains("Arial 222", t);
        Assert.DoesNotContain("Arial 111", t);

        await s.Commands.ExecuteAsync(new ParagraphEditCommand(id, second, null));   // 원래 글로 되돌리기
        Assert.False(s.Content.HasEdits);
        Assert.Contains(ContentPdfFactory.LineArial, await On(() => PageText(s, id)));

        await s.Commands.UndoAsync();   // 되돌리기 취소 → 두 번째 편집
        Assert.Same(second, s.Content.For(id).OfType<ReplaceParagraphEdit>().Single());
        await s.Commands.UndoAsync();   // 대체 취소 → 첫 번째 편집
        Assert.Same(first, s.Content.For(id).OfType<ReplaceParagraphEdit>().Single());
        Assert.Contains("Arial 111", await On(() => PageText(s, id)));
    }

    [Fact]
    public async Task ReEdit_KeepsPositionInLog()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        int ar = await On(() => IndexOfText(s, id, "Arial"));
        var first = await On(() => Edit(s, id, "Arial 111", arial: ar));
        var after = new DeactivateEdit(s.Content.NewId(), ObjectRef.Original(await On(() => s.GetObjectIndex(id).Entries.First(x => x.Type == PdfPageObjectType.Image).Ref.OriginalIndex)));
        await s.Commands.ExecuteAsync(new ParagraphEditCommand(id, null, first));
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [after]));
        var second = await On(() => Edit(s, id, "Arial 222", arial: ar));
        await s.Commands.ExecuteAsync(new ParagraphEditCommand(id, first, second));
        var log = s.Content.For(id);
        Assert.Same(second, log[0]);
        Assert.Same(after, log[1]);
        await s.Commands.UndoAsync();
        Assert.Same(first, s.Content.For(id)[0]);
    }

    [Fact]
    public async Task AppLines_AreDeferredAndTargetable_LikeInsertAppText()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        int arial = await On(() => IndexOfText(s, id, "Arial"));
        var outline = ImmutableArray.Create(new OutlineSeg('M', new PdfPointF(10, 10)), new OutlineSeg('L', new PdfPointF(40, 10)),
            new OutlineSeg('L', new PdfPointF(40, 30)), new OutlineSeg('Z', default));
        var spec = new AppTextSpec("가", 10, new PdfColor(0, 0, 0), PdfMatrix.Identity, new PdfRectF(10, 10, 40, 30), outline, "", null!);
        long lineId = s.Content.NewId();
        var meta = new ParagraphMeta(new PdfRectF(0, 0, 1, 1), "", "", "Left", "", 12, null);
        var e = new ReplaceParagraphEdit(s.Content.NewId(), [ObjectRef.Original(arial)], [], [spec], [lineId], meta);
        // 앱 글꼴 줄을 나중에 옮긴다 — 줄이 들어간 뒤에 적용돼야 한다(건너뛰지 않음)
        var move = new TransformEdit(s.Content.NewId(), ObjectRef.Inserted(lineId), PdfMatrix.Translate(5, 0));
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [e, move]));
        await On(() =>
        {
            var r = s.GetReplay(id);
            Assert.Equal(0, r.Skipped);
            var o = r.Resolve(ObjectRef.Inserted(lineId))!;
            Assert.Equal(PdfPageObjectType.Path, o.Type);
            Assert.Equal(15, o.Bounds.Left, 1);
            return 0;
        });
    }

    [Fact]
    public async Task DeleteEditedPage_Undo_EditsBack()
    {
        await using var s = await Open(pages: 2);
        var id = s.Pages[0].Id;
        var e = await On(() => Edit(s, id));
        await s.Commands.ExecuteAsync(new ParagraphEditCommand(id, null, e));
        await s.Commands.ExecuteAsync(new DeletePagesCommand([id]));
        Assert.Single(s.Pages);
        await s.Commands.UndoAsync();
        await On(() => { Assert.Contains(NewText, PageText(s, id)); return 0; });
    }

    [Fact]
    public async Task MoveEditedPage_EditsFollow()
    {
        await using var s = await Open(pages: 3);
        var id = s.Pages[0].Id;
        var e = await On(() => Edit(s, id));
        await s.Commands.ExecuteAsync(new ParagraphEditCommand(id, null, e));
        await s.Commands.ExecuteAsync(new MovePagesCommand([id], 2));
        await On(() =>
        {
            Assert.Contains(NewText, PageText(s, id));
            Assert.DoesNotContain(NewText, PageText(s, s.Pages[0].Id));
            return 0;
        });
    }

    [Fact]
    public async Task RotateEditedPage_EditsStayInPageSpace()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        var e = await On(() => Edit(s, id));
        await s.Commands.ExecuteAsync(new ParagraphEditCommand(id, null, e));
        var before = await On(() => s.GetObjectIndex(id).Entries.Single(x => x.Ref.IsInserted).Bounds);
        await s.Commands.ExecuteAsync(new RotatePagesCommand([id], 1));
        var after = await On(() => s.GetObjectIndex(id).Entries.Single(x => x.Ref.IsInserted).Bounds);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task NoParagraphEdits_NoCost_ReplayIsOriginalObjects()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        await On(() =>
        {
            var r = s.GetReplay(id);
            Assert.Empty(r.Inserted);
            Assert.Equal(0, r.Skipped);
            return 0;
        });
        Assert.Empty(s.Content.EditedPages);
    }
}
