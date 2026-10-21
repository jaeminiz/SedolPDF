using Pdf.Native;
using PdfEditor.Document.Content;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;

namespace PdfEditor.Document.Tests.Content;

/// <summary>M4 Task 3: 편집 기록, 쪽 불러올 때 다시 적용(replay), 명령·실행 취소, 객체 목록.</summary>
public sealed class ContentEditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-content-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public ContentEditTests() => Directory.CreateDirectory(_dir);

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

    private static int IndexOfType(DocumentSession s, PageId id, PdfPageObjectType type) =>
        s.GetObjectIndex(id).Entries.First(e => e.Type == type && !e.Ref.IsInserted).Ref.OriginalIndex;

    private static int IndexOfText(DocumentSession s, PageId id, string startsWith) =>
        s.GetObjectIndex(id).Entries.First(e => e.Type == PdfPageObjectType.Text && (e.TextPreview ?? "").StartsWith(startsWith)).Ref.OriginalIndex;

    private static string PageText(DocumentSession s, PageId id)
    {
        var tp = s.GetLoadedText(id);
        return tp.GetText(0, tp.CharCount);
    }

    private static byte[] Render(DocumentSession s, PageId id)
    {
        var page = s.GetLoadedPage(id);
        return page.RenderTile(0.5, 0, 0, (int)Math.Ceiling(page.SizePt.Width * 0.5), (int)Math.Ceiling(page.SizePt.Height * 0.5), false);
    }

    /// <summary>이미지 지우기 + 사각형 50pt 오른쪽으로 + Arial 줄을 원래 글꼴로 바꾸기(원래 줄 끄기 + 새 줄).</summary>
    private static ContentEditCommand SampleEdits(DocumentSession s, PageId id)
    {
        int img = IndexOfType(s, id, PdfPageObjectType.Image);
        int rect = s.GetObjectIndex(id).Entries.Where(e => e.Type == PdfPageObjectType.Path).OrderBy(e => e.Bounds.Width).Last().Ref.OriginalIndex;
        int arial = IndexOfText(s, id, "Arial");
        var st = s.Content;
        return new ContentEditCommand(id,
        [
            new DeactivateEdit(st.NewId(), ObjectRef.Original(img)),
            new TransformEdit(st.NewId(), ObjectRef.Original(rect), PdfMatrix.Translate(50, 0)),
            new InsertTextLikeEdit(st.NewId(), ObjectRef.Original(arial), "Arial 321 CBA"),
            new DeactivateEdit(st.NewId(), ObjectRef.Original(arial)),
        ]);
    }

    [Fact]
    public async Task Replay_DeactivateTransformInsert_AppliedOnEveryLoad()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        var (cmd, rectBefore) = await On(() =>
        {
            var c = SampleEdits(s, id);
            return (c, s.GetObjectIndex(id).Entries.Where(e => e.Type == PdfPageObjectType.Path).OrderBy(e => e.Bounds.Width).Last().Bounds);
        });
        int v0 = s.Pages[0].Version;
        await s.Commands.ExecuteAsync(cmd);
        Assert.True(s.Pages[0].Version > v0);   // 타일·썸네일 다시 그림 (Thumbnail_ReflectsEdit)
        await On(() =>
        {
            var idx = s.GetObjectIndex(id);
            Assert.DoesNotContain(idx.Entries, e => e.Type == PdfPageObjectType.Image);
            var rect = idx.Entries.Where(e => e.Type == PdfPageObjectType.Path).OrderBy(e => e.Bounds.Width).Last().Bounds;
            Assert.Equal(rectBefore.Left + 50, rect.Left, 0.5f);
            Assert.Contains(idx.Entries, e => e.Ref.IsInserted && e.TextPreview == "Arial 321 CBA");
            string text = PageText(s, id);
            Assert.Contains("Arial 321 CBA", text);           // Search_FindsReplacedText
            Assert.DoesNotContain(ContentPdfFactory.LineArial, text);   // 끈 원래 줄은 검색에 안 나온다
            return 0;
        });
    }

    [Fact]
    public async Task Undo_RemovesEdits_PageBack_RedoSameResult()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        var original = await On(() => Render(s, id));
        var cmd = await On(() => SampleEdits(s, id));
        await s.Commands.ExecuteAsync(cmd);
        var edited = await On(() => Render(s, id));
        Assert.NotEqual(original, edited);
        await s.Commands.UndoAsync();
        Assert.Equal(original, await On(() => Render(s, id)));
        Assert.False(s.Content.HasEdits);
        await s.Commands.RedoAsync();
        Assert.Equal(edited, await On(() => Render(s, id)));
    }

    [Fact]
    public async Task NoEdits_ReplayIsOriginalObjects()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        await On(() =>
        {
            var r = s.GetReplay(id);
            Assert.Empty(r.Inserted);
            Assert.Equal(s.GetLoadedPage(id).ObjectCount, r.Originals.Count);
            Assert.Equal(0, r.Skipped);
            return 0;
        });
        Assert.False(s.Content.HasEdits);
    }

    [Fact]
    public async Task DeleteEditedPage_Undo_EditsBack()
    {
        await using var s = await Open(pages: 2);
        var id = s.Pages[0].Id;
        await s.Commands.ExecuteAsync(await On(() => SampleEdits(s, id)));
        await s.Commands.ExecuteAsync(new DeletePagesCommand([id]));
        Assert.Single(s.Pages);
        await s.Commands.UndoAsync();
        Assert.Equal(id, s.Pages[0].Id);
        await On(() => { Assert.Contains("Arial 321 CBA", PageText(s, id)); return 0; });
    }

    [Fact]
    public async Task MoveEditedPage_EditsFollow()
    {
        await using var s = await Open(pages: 3);
        var id = s.Pages[0].Id;
        await s.Commands.ExecuteAsync(await On(() => SampleEdits(s, id)));
        await s.Commands.ExecuteAsync(new MovePagesCommand([id], 2));
        Assert.Equal(id, s.Pages[2].Id);
        await On(() =>
        {
            Assert.Contains("Arial 321 CBA", PageText(s, id));
            Assert.DoesNotContain("Arial 321 CBA", PageText(s, s.Pages[0].Id));
            return 0;
        });
    }

    [Fact]
    public async Task RotateEditedPage_EditsStayInPageSpace()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        await s.Commands.ExecuteAsync(await On(() => SampleEdits(s, id)));
        var before = await On(() => s.GetObjectIndex(id).Entries.Single(e => e.Ref.IsInserted).Bounds);
        await s.Commands.ExecuteAsync(new RotatePagesCommand([id], 1));
        var after = await On(() => s.GetObjectIndex(id).Entries.Single(e => e.Ref.IsInserted).Bounds);
        Assert.Equal(before, after);   // 쪽 회전(/Rotate)은 쪽 좌표의 객체 경계를 바꾸지 않는다
    }

    [Fact]
    public async Task PageHandleEvicted_ReloadReplays()
    {
        await using var s = await Open(pages: 12);
        var id = s.Pages[0].Id;
        await s.Commands.ExecuteAsync(await On(() => SampleEdits(s, id)));
        await On(() =>
        {
            for (int i = 1; i < 12; i++) _ = s.GetLoadedPage(s.Pages[i].Id);   // 캐시(8쪽)에서 첫 쪽이 밀린다
            Assert.Contains("Arial 321 CBA", PageText(s, id));
            Assert.DoesNotContain(s.GetObjectIndex(id).Entries, e => e.Type == PdfPageObjectType.Image);
            return 0;
        });
    }

    [Fact]
    public async Task LiveDocument_ContentNeverRewritten()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        await s.Commands.ExecuteAsync(await On(() => SampleEdits(s, id)));
        string copy = Path.Combine(_dir, "copy.pdf");
        await On(() => { s.Document.SaveCopy(copy); return 0; });
        // SaveCopy는 편집을 확정하지 않는다(확정은 출력 단계의 몫, Task 4): 사본에는 원래 이미지가 그대로 있다
        await On(() =>
        {
            using var d = PdfDocument.Open(_t, copy);
            using var p = d.LoadPage(0);
            Assert.Contains(Enumerable.Range(0, p.ObjectCount), i => p.GetObject(i).Type == PdfPageObjectType.Image);
            return 0;
        });
    }

    [Fact]
    public async Task ObjectIndex_FrameLast_TopmostFirst()
    {
        string p = Path.Combine(_dir, "dwg.pdf");
        ContentPdfFactory.CreateDrawingLike(p);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        var id = s.Pages[0].Id;
        await On(() =>
        {
            var idx = s.GetObjectIndex(id);
            Assert.Contains(idx.Entries, e => e.IsFrame);
            // 빈 곳(속 빈 테두리 안쪽): 이제 아무것도 안 맞는다 — 테두리는 선 위를 눌러야 맞는다
            Assert.Empty(idx.HitTest(new PdfPointF(1100, 100), 1));
            // 테두리 선 위(위에서 20pt = 쪽 아래에서 822)
            var onLine = idx.HitTest(new PdfPointF(600, 842 - 20), 1);
            Assert.Single(onLine);
            Assert.True(onLine[0].IsFrame);
            // 반투명 사각형 위: 그 사각형만(속 빈 테두리는 선 위가 아니면 안 맞는다)
            var onRect = idx.HitTest(new PdfPointF(750, 842 - 310), 1);
            Assert.False(onRect[0].IsFrame);
            Assert.DoesNotContain(onRect, e => e.IsFrame);
            // 테두리 선과 다른 객체가 겹치는 곳이면 테두리는 맨 뒤
            var lineHits = idx.HitTest(new PdfPointF(600, 842 - 20), 1);
            Assert.True(lineHits[^1].IsFrame);
            return 0;
        });
    }

    /// <summary>사용자 보고: 안쪽 선·도형을 눌러도 바깥 테두리(쪽의 90 % 미만이라 IsFrame도 아님)가 먼저 선택됨.</summary>
    [Fact]
    public async Task ObjectIndex_HollowFrame_DoesNotStealInnerClicks()
    {
        string p = Path.Combine(_dir, "frame.pdf");
        ContentPdfFactory.CreateFrameWithInner(p);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        var id = s.Pages[0].Id;
        await On(() =>
        {
            var idx = s.GetObjectIndex(id);
            var frame = idx.Entries.Single(e => e.Type == PdfPageObjectType.Path && e.Bounds.Width > 500);
            Assert.False(frame.IsFrame);   // 89 % — 예전에는 이 때문에 먼저 잡혔다
            Assert.NotNull(frame.Geometry);
            PdfPointF P(float x, float yTop) => new(x, 842 - yTop);   // 위에서 아래 좌표 → 쪽 좌표

            // 안쪽 대각선 위: 대각선이 먼저(그 다음에도 테두리는 없음)
            var line = idx.HitTest(P(200, 200), 2);
            Assert.Single(line);
            Assert.NotEqual(frame.Ref, line[0].Ref);
            // 대각선의 경계 상자 모서리 안(선에서 먼 곳): 아무것도 안 맞는다
            Assert.Empty(idx.HitTest(P(120, 280), 2));
            // 속 빈 안쪽 사각형 안쪽: 안 맞고, 그 선 위는 맞는다
            Assert.Empty(idx.HitTest(P(425, 175), 2));
            Assert.Single(idx.HitTest(P(350, 175), 2));
            // 칠한 사각형은 안쪽도 맞는다
            Assert.Single(idx.HitTest(P(400, 450), 2));
            // 테두리는 그 선 위를 누를 때만
            var edge = idx.HitTest(P(20, 400), 2);
            Assert.Single(edge);
            Assert.Equal(frame.Ref, edge[0].Ref);
            Assert.Empty(idx.HitTest(P(300, 700), 2));
            // 확대(허용 오차 축소): 대각선에서 3pt 떨어진 점은 허용 4pt면 맞고 0.5pt면 안 맞는다
            var near = P(200 + 3 / MathF.Sqrt(2), 200 - 3 / MathF.Sqrt(2));
            Assert.Single(idx.HitTest(near, 4));
            Assert.Empty(idx.HitTest(near, 0.5f));
            return 0;
        });
    }

    [Fact]
    public async Task ObjectIndex_ManyTextObjects_SkipsPreviews_FullTextStillReadable()
    {
        string p = Path.Combine(_dir, "many.pdf");
        ContentPdfFactory.CreateManyTextLines(p, PageObjectIndex.TextPreviewObjectLimit + 50);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        var id = s.Pages[0].Id;
        var (previews, text) = await On(() =>
        {
            var idx = s.GetObjectIndex(id);
            var first = idx.Entries.First(e => e.Type == PdfPageObjectType.Text);
            return (idx.Entries.Count(e => e.TextPreview is not null), s.GetObjectText(id, first.Ref));
        });
        Assert.Equal(0, previews);   // 큰 도면은 목록 만들기 때 글을 읽지 않는다(5,000줄에서 1.7 s)
        Assert.StartsWith("LINE", text);   // 편집할 때 그 줄만 따로 읽는다
    }
}
