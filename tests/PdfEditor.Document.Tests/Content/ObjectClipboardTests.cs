using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.TestFixtures;

namespace PdfEditor.Document.Tests.Content;

/// <summary>0.9.0 Task 6: 개체 클립보드 — 복사 재료, 붙여넣기 편집, 놓는 자리.</summary>
public sealed class ObjectClipboardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-clip-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public ObjectClipboardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<DocumentSession> Open()
    {
        string p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        ContentPdfFactory.CreateDocumentLike(p);
        return await DocumentSession.OpenAsync(_t, p, null);
    }

    private static readonly PdfRectF Page = new(0, 0, 595, 842);

    // ---- 놓는 자리 ----

    [Theory]
    [InlineData(0, 10f, -10f)]
    [InlineData(1, 10f, 10f)]
    [InlineData(2, -10f, 10f)]
    [InlineData(3, -10f, -10f)]
    public void PasteOffset_FollowsScreenDirection(int rotation, float dx, float dy)
    {
        var m = PasteOffset.Compute(true, 1, rotation, new PdfRectF(100, 100, 150, 150), Page);
        Assert.Equal(dx, m.E, 3);
        Assert.Equal(dy, m.F, 3);
    }

    [Fact]
    public void PasteOffset_SecondPaste_Doubles()
    {
        var m = PasteOffset.Compute(true, 2, 0, new PdfRectF(100, 100, 150, 150), Page);
        Assert.Equal(20f, m.E, 3);
        Assert.Equal(-20f, m.F, 3);
    }

    [Fact]
    public void PasteOffset_OtherPage_ClampsIntoPage()
    {
        var m = PasteOffset.Compute(false, 1, 0, new PdfRectF(580, 10, 600, 30), Page);
        Assert.Equal(-5f, m.E, 3);
        Assert.Equal(0f, m.F, 3);
    }

    [Fact]
    public void PasteOffset_OtherPage_InsideStaysPut()
    {
        var m = PasteOffset.Compute(false, 1, 0, new PdfRectF(100, 100, 150, 150), Page);
        Assert.Equal(PdfMatrix.Identity, m);
    }

    // ---- 복사 ----

    private static ObjectRef FirstOf(DocumentSession s, PageId id, PdfPageObjectType type, Func<PageObjectEntry, bool>? where = null) =>
        s.GetObjectIndex(id).Entries.Where(e => e.Type == type && !e.Ref.IsInserted && (where?.Invoke(e) ?? true)).OrderBy(e => e.Order).First().Ref;

    private Task Exec(DocumentSession s, PageId id, params ContentEdit[] edits) => s.Commands.ExecuteAsync(new ContentEditCommand(id, edits));

    [Fact]
    public async Task Copy_ThenMoveOriginal_PasteUsesCopyTimeLook()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        var r = await _t.InvokeAsync(() => FirstOf(s, id, PdfPageObjectType.Path, e => e.Bounds.Width < 400));
        var copy = await _t.InvokeAsync(() => ObjectCopyBuilder.Build(s, id, [r]));
        Assert.Equal(0, copy.Skipped);
        await Exec(s, id, new TransformEdit(s.Content.NewId(), r, PdfMatrix.Translate(50, 0)));
        var (edits, newRefs) = ObjectPasteBuilder.Build(s.Content, copy.Items, PdfMatrix.Translate(10, -10));
        var d = Assert.IsType<DuplicateObjectEdit>(Assert.Single(edits));
        Assert.Equal(r.OriginalIndex, d.SourceIndex);
        Assert.Equal(PdfMatrix.Translate(10, -10), d.Then);   // 복사 뒤 옮긴 +50은 들어가지 않는다
        Assert.Equal(ObjectRef.Inserted(d.Id), Assert.Single(newRefs));
    }

    [Fact]
    public async Task Copy_FoldsEarlierTransformsAndTextColor()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        var r = await _t.InvokeAsync(() => FirstOf(s, id, PdfPageObjectType.Text));
        await Exec(s, id,
            new TransformEdit(s.Content.NewId(), r, PdfMatrix.Translate(5, 0)),
            new TransformEdit(s.Content.NewId(), r, PdfMatrix.Translate(0, 7)),
            new SetTextColorEdit(s.Content.NewId(), r, new PdfColor(255, 0, 0)));
        var copy = await _t.InvokeAsync(() => ObjectCopyBuilder.Build(s, id, [r]));
        var c = Assert.IsType<OriginalClip>(Assert.Single(copy.Items));
        Assert.Equal(PdfMatrix.Translate(5, 7), c.Then);
        Assert.Equal(new PdfColor(255, 0, 0), c.TextColor);
        var (edits, _) = ObjectPasteBuilder.Build(s.Content, copy.Items, PdfMatrix.Identity);
        Assert.IsType<DuplicateObjectEdit>(edits[0]);
        var col = Assert.IsType<SetTextColorEdit>(edits[1]);
        Assert.Equal(ObjectRef.Inserted(edits[0].Id), col.Target);
    }

    [Fact]
    public async Task Copy_OfDuplicate_PointsAtOriginalSource()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        var r = await _t.InvokeAsync(() => FirstOf(s, id, PdfPageObjectType.Path, e => e.Bounds.Width < 400));
        long did = s.Content.NewId();
        await Exec(s, id, new DuplicateObjectEdit(did, id, r.OriginalIndex, PdfMatrix.Translate(10, 0)),
            new TransformEdit(s.Content.NewId(), ObjectRef.Inserted(did), PdfMatrix.Translate(5, 0)));
        var copy = await _t.InvokeAsync(() => ObjectCopyBuilder.Build(s, id, [ObjectRef.Inserted(did)]));
        var c = Assert.IsType<OriginalClip>(Assert.Single(copy.Items));
        Assert.Equal(r.OriginalIndex, c.SourceIndex);
        Assert.Equal(id, c.SourcePage);
        Assert.Equal(PdfMatrix.Translate(15, 0), c.Then);
    }

    [Fact]
    public async Task Copy_ParagraphLine_IsSkipped()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        var (r, placement) = await _t.InvokeAsync(() =>
        {
            var rr = FirstOf(s, id, PdfPageObjectType.Text, e => (e.TextPreview ?? "").StartsWith("Arial"));
            return (rr, s.GetReplay(id).Resolve(rr)!.Matrix);
        });
        long lineId = s.Content.NewId();
        var line = new ParagraphTextLine(lineId, "Hello", placement, 14, new PdfColor(0, 0, 0), r, 0, 100, 0, 0);
        var para = new ReplaceParagraphEdit(s.Content.NewId(), [r], [line], [], [],
            new ParagraphMeta(new PdfRectF(0, 0, 10, 10), "a", "b", "left", "k", 0, null));
        await Exec(s, id, para);
        var copy = await _t.InvokeAsync(() =>
        {
            Assert.Contains(s.GetObjectIndex(id).Entries, e => e.Ref == ObjectRef.Inserted(lineId));
            return ObjectCopyBuilder.Build(s, id, [ObjectRef.Inserted(lineId)]);
        });
        Assert.Equal(1, copy.Skipped);
        Assert.Empty(copy.Items);
    }

    // ---- 붙여넣기 ----

    [Fact]
    public async Task Paste_Shape_NewIdAndLatestStyle()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        long sid = s.Content.NewId();
        var spec = new ShapeSpec(ShapeKind.Rectangle, new PdfRectF(100, 500, 200, 560), default, default, ShapeStyle.Default);
        var style = new ShapeStyle(new PdfColor(0, 0, 255), 3f, new PdfColor(255, 255, 0));
        await Exec(s, id, new InsertShapeEdit(sid, spec), new SetShapeStyleEdit(s.Content.NewId(), ObjectRef.Inserted(sid), style));
        var copy = await _t.InvokeAsync(() => ObjectCopyBuilder.Build(s, id, [ObjectRef.Inserted(sid)]));
        var (edits, refs) = ObjectPasteBuilder.Build(s.Content, copy.Items, PdfMatrix.Translate(10, -10));
        var ins = Assert.IsType<InsertShapeEdit>(edits[0]);
        Assert.NotEqual(sid, ins.Id);
        Assert.Equal(style, ins.Shape.Style);
        var mv = Assert.IsType<TransformEdit>(edits[1]);
        Assert.Equal(ObjectRef.Inserted(ins.Id), mv.Target);
        Assert.Equal(PdfMatrix.Translate(10, -10), mv.Then);
        Assert.Equal(ObjectRef.Inserted(ins.Id), Assert.Single(refs));
    }

    [Fact]
    public async Task Paste_Image_PlacementIncludesOffset()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        string asset = s.Assets.AddImage(TestImages.Png(20, 20, (_, _) => (0, 0, 255, 255)));
        long iid = s.Content.NewId();
        var placement = new PdfMatrix(100, 0, 0, 80, 300, 500);
        await Exec(s, id, new InsertImageEdit(iid, asset, placement));
        var copy = await _t.InvokeAsync(() => ObjectCopyBuilder.Build(s, id, [ObjectRef.Inserted(iid)]));
        var (edits, _) = ObjectPasteBuilder.Build(s.Content, copy.Items, PdfMatrix.Translate(10, -10));
        var im = Assert.IsType<InsertImageEdit>(Assert.Single(edits));
        Assert.Equal(asset, im.AssetId);
        Assert.Equal(310f, im.Placement.E, 3);
        Assert.Equal(490f, im.Placement.F, 3);
        Assert.Equal(100f, im.Placement.A, 3);
    }

    [Fact]
    public async Task Paste_KeepsDrawingOrder()
    {
        await using var s = await Open();
        var id = s.Pages[0].Id;
        var refs = await _t.InvokeAsync(() => s.GetObjectIndex(id).Entries.Where(e => !e.Ref.IsInserted && e.Type is PdfPageObjectType.Path or PdfPageObjectType.Image)
            .OrderBy(e => e.Order).Take(3).Select(e => e.Ref).ToList());
        Assert.Equal(3, refs.Count);
        var shuffled = new List<ObjectRef> { refs[2], refs[0], refs[1] };
        var copy = await _t.InvokeAsync(() => ObjectCopyBuilder.Build(s, id, shuffled));
        var (edits, _) = ObjectPasteBuilder.Build(s.Content, copy.Items, PdfMatrix.Identity);
        Assert.Equal(refs.Select(r => r.OriginalIndex), edits.OfType<DuplicateObjectEdit>().Select(d => d.SourceIndex));
    }
}
