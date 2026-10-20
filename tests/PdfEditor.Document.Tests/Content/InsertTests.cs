using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Document.Tests.Content;

/// <summary>M4 Task 6: 새 이미지·도형(PDFium 객체), 도형 스타일, 넣은 객체 옮기기·지우기.</summary>
public sealed class InsertTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-insert-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public InsertTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    private async Task<DocumentSession> Open(string? password = null)
    {
        string p = P(Guid.NewGuid().ToString("N") + ".pdf");
        ContentPdfFactory.CreateDocumentLike(p);
        if (password is not null)
        {
            using var d = PdfReader.Open(p, PdfDocumentOpenMode.Modify);
            d.SecuritySettings.UserPassword = password;
            d.SecuritySettings.OwnerPassword = password + "-o";
            d.Save(p);
        }
        return await DocumentSession.OpenAsync(_t, p, password);
    }

    private static byte[] Pixel(PdfPage page, float xPt, float yPt) => page.RenderTile(1, (int)xPt, (int)(page.SizePt.Height - yPt), 1, 1, false);

    private static async Task Exec(DocumentSession s, params ContentEdit[] edits) =>
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, edits));

    private static readonly PdfMatrix Box = new(100, 0, 0, 80, 400, 50);   // 100×80pt, 왼쪽 아래 (400, 50)

    [Fact]
    public async Task InsertImage_Png_RendersAndSaves()
    {
        await using var s = await Open();
        string id = s.Assets.AddImage(TestImages.Png(20, 20, (_, _) => (0, 0, 255, 255)));   // 파랑
        await Exec(s, new InsertImageEdit(s.Content.NewId(), id, Box));
        var px = await _t.InvokeAsync(() => Pixel(s.GetLoadedPage(s.Pages[0].Id), 450, 90));
        Assert.True(px[0] > 200 && px[2] < 60, "blue on screen");
        await new SaveService([]).SaveAsync(s, P("png.pdf"));
        var saved = await _t.InvokeAsync(() => { using var d = PdfDocument.Open(_t, P("png.pdf")); using var p = d.LoadPage(0); return Pixel(p, 450, 90); });
        Assert.True(saved[0] > 200 && saved[2] < 60, "blue in file");
    }

    [Fact]
    public async Task InsertImage_Jpeg_BytesKept()
    {
        await using var s = await Open();
        var jpeg = TestJpeg.Create(32, 32, (x, y) => (220, 20, 20));
        string id = s.Assets.AddImage(jpeg);
        await Exec(s, new InsertImageEdit(s.Content.NewId(), id, Box));
        await new SaveService([]).SaveAsync(s, P("jpg.pdf"));
        using var doc = PdfReader.Open(P("jpg.pdf"), PdfDocumentOpenMode.Import);
        var xobjs = doc.Pages[0].Resources.Elements.GetDictionary("/XObject")!.Elements.Values
            .Select(v => (v as PdfSharp.Pdf.Advanced.PdfReference)?.Value as PdfSharp.Pdf.PdfDictionary).Where(d => d is not null).ToList();
        Assert.Contains(xobjs, d => d!.Elements["/Filter"]?.ToString()?.Contains("DCTDecode") == true && d.Stream.Value.AsSpan().SequenceEqual(jpeg));
    }

    [Fact]
    public void InsertImage_TooLargeOrUnsupported_Rejected()
    {
        var assets = new AnnotationAssets();
        Assert.Throws<ArgumentException>(() => assets.AddImage(TestImages.PngHeaderOnly(20000, 20000)));
        // 16비트 PNG는 쪽 그림으로 풀 수 없다(넣기 전 검사로 걸러 낸다)
        var png16 = TestImages.PngRaw(2, 2, colorType: 2, bitDepth: 16, interlace: 0, raw: new byte[2 * (1 + 2 * 6)]);
        Assert.NotNull(PngDecoder.Unsupported(png16));
        Assert.Null(PngDecoder.Unsupported(TestImages.Png(2, 2, (_, _) => (1, 2, 3, 255))));
    }

    [Theory]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.Ellipse)]
    [InlineData(ShapeKind.Line)]
    public async Task InsertShape_RenderAndSave(ShapeKind kind)
    {
        await using var s = await Open();
        var style = new ShapeStyle(new PdfColor(0, 0, 0), 2, kind == ShapeKind.Line ? null : new PdfColor(0, 160, 0));
        var spec = new ShapeSpec(kind, new PdfRectF(400, 50, 500, 130), new PdfPointF(400, 90), new PdfPointF(500, 90), style);
        await Exec(s, new InsertShapeEdit(s.Content.NewId(), spec));
        await new SaveService([]).SaveAsync(s, P("shape.pdf"));
        var px = await _t.InvokeAsync(() => { using var d = PdfDocument.Open(_t, P("shape.pdf")); using var p = d.LoadPage(0); return Pixel(p, 450, 90); });
        if (kind == ShapeKind.Line) Assert.True(px[0] < 80 && px[1] < 80 && px[2] < 80, "black line through the middle");
        else Assert.True(px[1] > 120 && px[0] < 60 && px[2] < 60, "green fill in the middle");
    }

    [Fact]
    public async Task InsertedObject_MoveStyleDelete_Undo()
    {
        await using var s = await Open();
        var spec = new ShapeSpec(ShapeKind.Rectangle, new PdfRectF(400, 50, 500, 130), default, default, ShapeStyle.Default with { Fill = new PdfColor(0, 160, 0) });
        var ins = new InsertShapeEdit(s.Content.NewId(), spec);
        await Exec(s, ins);
        var r = ObjectRef.Inserted(ins.Id);
        await Exec(s, new TransformEdit(s.Content.NewId(), r, PdfMatrix.Translate(0, 300)));
        await Exec(s, new SetShapeStyleEdit(s.Content.NewId(), r, ShapeStyle.Default with { Fill = new PdfColor(0, 0, 200) }));
        var id = s.Pages[0].Id;
        var moved = await _t.InvokeAsync(() => (s.GetObjectIndex(id).Entries.Single(e => e.Ref == r).Bounds, Pixel(s.GetLoadedPage(id), 450, 390)));
        Assert.Equal(350, moved.Bounds.Bottom, 2f);
        Assert.True(moved.Item2[0] > 150 && moved.Item2[1] < 60, "blue after style change");
        await Exec(s, new DeactivateEdit(s.Content.NewId(), r));
        Assert.DoesNotContain(await _t.InvokeAsync(() => s.GetObjectIndex(id).Entries), e => e.Ref == r);
        await s.Commands.UndoAsync();   // 지우기 취소
        Assert.Contains(await _t.InvokeAsync(() => s.GetObjectIndex(id).Entries), e => e.Ref == r);
    }

    [Fact]
    public async Task Encrypted_InsertImageAndShape_Allowed()
    {
        await using var s = await Open("pw");
        string id = s.Assets.AddImage(TestImages.Png(4, 4, (_, _) => (0, 0, 255, 255)));
        await Exec(s, new InsertImageEdit(s.Content.NewId(), id, Box),
            new InsertShapeEdit(s.Content.NewId(), new ShapeSpec(ShapeKind.Rectangle, new PdfRectF(100, 700, 200, 780), default, default, ShapeStyle.Default)));
        await new SaveService([]).SaveAsync(s, P("enc.pdf"));
        var px = await _t.InvokeAsync(() => { using var d = PdfDocument.Open(_t, P("enc.pdf"), "pw"); using var p = d.LoadPage(0); return Pixel(p, 450, 90); });
        Assert.True(px[0] > 200 && px[2] < 60);
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => _t.InvokeAsync(() => { using var d = PdfDocument.Open(_t, P("enc.pdf")); return 0; }));
    }
}
