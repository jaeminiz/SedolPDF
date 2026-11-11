using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.TestFixtures;

namespace PdfEditor.Document.Tests.Annotations;

/// <summary>0.9.0 Task 8: 주석 복사·붙여넣기 모델(AnnotationCopyBuilder, AnnotationPasteBuilder).</summary>
public sealed class AnnotationClipboardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-annclip-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    private static readonly PdfColor Red = new(229, 55, 45);

    public AnnotationClipboardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<DocumentSession> Open(int pages = 1)
    {
        string p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        TestPdfFactory.CreateBlank(p, pages, 300, 300);
        var s = await DocumentSession.OpenAsync(_t, p, null);
        await s.AnnotationsReady;
        return s;
    }

    private static Annotation Square(DocumentSession s) => new()
    {
        Id = s.Annotations.NewId(), Kind = AnnotKind.Square, Rect = new PdfRectF(20, 20, 80, 60), Color = Red,
        Author = "old", CreatedAt = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), Name = "oldname", Contents = "memo",
    };

    private static DateTimeOffset Now => new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Paste_NewIdNameAuthorAndTime()
    {
        await using var s = await Open();
        var page = s.Pages[0].Id;
        var a = Square(s);
        var cmd = AnnotationPasteBuilder.Build(page, 0, 0, [a], PdfMatrix.Translate(10, -10), s.Annotations.NewId, "me", Now);
        await s.Commands.ExecuteAsync(cmd);
        var added = s.Annotations.Get(page).Items.Select(v => v.Data).Single();
        Assert.NotEqual(a.Id, added.Id);
        Assert.NotEqual("oldname", added.Name);
        Assert.Equal(32, added.Name!.Length);
        Assert.Matches("^[0-9a-f]{32}$", added.Name);
        Assert.Equal("me", added.Author);
        Assert.Equal(Now, added.CreatedAt);
        Assert.Equal(Now, added.ModifiedAt);
        Assert.Equal("memo", added.Contents);
    }

    [Fact]
    public async Task Paste_Translates_RectVerticesInkAndTextBox()
    {
        await using var s = await Open();
        var page = s.Pages[0].Id;
        var items = new[]
        {
            Square(s),
            new Annotation { Id = s.Annotations.NewId(), Kind = AnnotKind.Polygon, Rect = new PdfRectF(0, 0, 50, 50), Vertices = [new(0, 0), new(50, 0), new(25, 50)] },
            new Annotation { Id = s.Annotations.NewId(), Kind = AnnotKind.Ink, Rect = new PdfRectF(0, 0, 40, 40), InkStrokes = [[new(0, 0), new(40, 40)]] },
            new Annotation { Id = s.Annotations.NewId(), Kind = AnnotKind.FreeText, Intent = FreeTextIntent.Callout, Rect = new PdfRectF(100, 100, 160, 130), TextBox = new PdfRectF(100, 100, 160, 130), Vertices = [new(90, 90), new(100, 100)] },
        };
        await s.Commands.ExecuteAsync(AnnotationPasteBuilder.Build(page, 0, 0, items, PdfMatrix.Translate(10, -10), s.Annotations.NewId, "me", Now));
        var got = s.Annotations.Get(page).Items.Select(v => v.Data).ToList();
        Assert.Equal(4, got.Count);
        Assert.Equal(30f, got[0].Rect.Left, 3); Assert.Equal(10f, got[0].Rect.Bottom, 3);
        Assert.Equal(new PdfPointF(10, -10), got[1].Vertices[0]);
        Assert.Equal(new PdfPointF(35, 40), got[1].Vertices[2]);
        Assert.Equal(new PdfPointF(10, -10), got[2].InkStrokes[0][0]);
        Assert.Equal(110f, got[3].TextBox!.Value.Left, 3);
        Assert.Equal(new PdfPointF(100, 80), got[3].Vertices[0]);
    }

    [Fact]
    public async Task Copy_StrikeOutCaretGroup_CopiedTogether_ReplyLinkRewired()
    {
        await using var s = await Open();
        var page = s.Pages[0].Id;
        var strike = new Annotation
        {
            Id = s.Annotations.NewId(), Kind = AnnotKind.StrikeOut, Rect = new PdfRectF(40, 100, 140, 115), Color = Red,
            Quads = [new PdfQuadF(new(40, 115), new(140, 115), new(40, 100), new(140, 100))],
        };
        var caret = new Annotation
        {
            Id = s.Annotations.NewId(), Kind = AnnotKind.Caret, Rect = new PdfRectF(136, 96, 152, 116), Contents = "x",
            InReplyTo = strike.Id, ReplyKind = ReplyType.Group,
        };
        await s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(page, [new(null, strike), new(null, caret)]));
        var copy = AnnotationCopyBuilder.Build(s.Annotations.Get(page), [caret.Id]);   // 묶음 구성원 하나만 골라도 묶음째
        Assert.Equal(0, copy.Skipped);
        Assert.Equal(2, copy.Items.Length);
        Assert.Equal(strike.Id, copy.Items[0].Id);
        var cmd = AnnotationPasteBuilder.Build(page, 0, 0, copy.Items, PdfMatrix.Translate(10, -10), s.Annotations.NewId, "me", Now);
        await s.Commands.ExecuteAsync(cmd);
        var all = s.Annotations.Get(page).Items.Select(v => v.Data).ToList();
        Assert.Equal(4, all.Count);
        var newStrike = all[2]; var newCaret = all[3];
        Assert.NotEqual(strike.Id, newStrike.Id);
        Assert.Equal(newStrike.Id, newCaret.InReplyTo);
        Assert.Equal(ReplyType.Group, newCaret.ReplyKind);
    }

    [Fact]
    public async Task Copy_UnderlineAndReply_AreSkipped()
    {
        await using var s = await Open();
        var page = s.Pages[0].Id;
        var sq = Square(s);
        var hi = new Annotation
        {
            Id = s.Annotations.NewId(), Kind = AnnotKind.Underline, Rect = new PdfRectF(10, 10, 90, 25),
            Quads = [new PdfQuadF(new(10, 25), new(90, 25), new(10, 10), new(90, 10))],
        };
        var reply = new Annotation { Id = s.Annotations.NewId(), Kind = AnnotKind.Text, Rect = new PdfRectF(10, 10, 30, 30), InReplyTo = sq.Id, ReplyKind = ReplyType.Reply };
        await s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(page, [new(null, sq), new(null, hi), new(null, reply)]));
        var copy = AnnotationCopyBuilder.Build(s.Annotations.Get(page), [hi.Id, reply.Id]);
        Assert.Equal(2, copy.Skipped);
        Assert.Empty(copy.Items);
        var ok = AnnotationCopyBuilder.Build(s.Annotations.Get(page), [sq.Id, hi.Id]);
        Assert.Single(ok.Items);
        Assert.Equal(1, ok.Skipped);
    }

    [Fact]
    public async Task Copy_RectHighlight_AreaHighlight_IsCopied_AndRotatesWithMatrix()
    {
        await using var s = await Open();
        var page = s.Pages[0].Id;
        var hi = new Annotation
        {
            Id = s.Annotations.NewId(), Kind = AnnotKind.Highlight, Rect = new PdfRectF(10, 10, 90, 25),
            Quads = [new PdfQuadF(new(10, 25), new(90, 25), new(10, 10), new(90, 10))],
        };
        await s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(page, [new(null, hi)]));
        var copy = AnnotationCopyBuilder.Build(s.Annotations.Get(page), [hi.Id]);
        Assert.Equal(0, copy.Skipped);
        Assert.Single(copy.Items);
        // 180° 돌린 쪽(폭 300, 높이 300)으로: (x,y) → (300-x, 300-y)
        var flip = new PdfMatrix(-1, 0, 0, -1, 300, 300);
        await s.Commands.ExecuteAsync(AnnotationPasteBuilder.Build(page, 2, 0, copy.Items, flip, s.Annotations.NewId, "me", Now));
        var pasted = s.Annotations.Get(page).Items.Select(v => v.Data).Last();
        Assert.Equal(210f, pasted.Rect.Left, 3); Assert.Equal(290f, pasted.Rect.Right, 3);
        Assert.Equal(275f, pasted.Rect.Bottom, 3); Assert.Equal(290f, pasted.Rect.Top, 3);
    }

    [Fact]
    public async Task Paste_StampImage_SharesImageId()
    {
        await using var s = await Open();
        var page = s.Pages[0].Id;
        var stamp = new Annotation
        {
            Id = s.Annotations.NewId(), Kind = AnnotKind.Stamp, Rect = new PdfRectF(10, 10, 60, 60),
            Stamp = new StampSpec(StampKind.Image, "", null, "img-key-1"),
        };
        await s.Commands.ExecuteAsync(AnnotationPasteBuilder.Build(page, 0, 0, [stamp], PdfMatrix.Translate(10, -10), s.Annotations.NewId, "me", Now));
        var got = s.Annotations.Get(page).Items.Select(v => v.Data).Single();
        Assert.Equal("img-key-1", got.Stamp!.ImageId);
    }

    [Fact]
    public async Task Paste_FreeTextOntoDifferentlyRotatedPage_GetsTargetRotation()
    {
        await using var s = await Open();
        var page = s.Pages[0].Id;
        var ft = new Annotation { Id = s.Annotations.NewId(), Kind = AnnotKind.FreeText, Rect = new PdfRectF(10, 10, 100, 40), Contents = "hi", Rotation = 0 };
        await s.Commands.ExecuteAsync(AnnotationPasteBuilder.Build(page, 1, 0, [ft], PdfMatrix.Identity, s.Annotations.NewId, "me", Now));
        Assert.Equal(90, s.Annotations.Get(page).Items.Select(v => v.Data).Single().Rotation);
        // 같은 회전이면 그대로
        var ft2 = ft with { Id = s.Annotations.NewId(), Rotation = 90 };
        await s.Commands.ExecuteAsync(AnnotationPasteBuilder.Build(page, 1, 1, [ft2], PdfMatrix.Identity, s.Annotations.NewId, "me", Now));
        Assert.Equal(90, s.Annotations.Get(page).Items.Select(v => v.Data).Last().Rotation);
    }

    [Fact]
    public async Task Paste_Undo_RemovesAll()
    {
        await using var s = await Open();
        var page = s.Pages[0].Id;
        var items = new[] { Square(s), Square(s) with { Id = s.Annotations.NewId() }, Square(s) with { Id = s.Annotations.NewId() } };
        await s.Commands.ExecuteAsync(AnnotationPasteBuilder.Build(page, 0, 0, items, PdfMatrix.Translate(10, -10), s.Annotations.NewId, "me", Now));
        Assert.Equal(3, s.Annotations.Get(page).Items.Length);
        await s.Commands.UndoAsync();
        Assert.Empty(s.Annotations.Get(page).Items);
    }
}

