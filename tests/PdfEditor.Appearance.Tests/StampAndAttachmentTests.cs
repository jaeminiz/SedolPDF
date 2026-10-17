using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Appearance.Tests.Fonts;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PdfSharpDocument = PdfSharp.Pdf.PdfDocument;

namespace PdfEditor.Appearance.Tests;

/// <summary>스탬프(기본·글자·그림)와 파일 첨부 주석: Painter, 패치 계획, PDFsharp, 다시 읽기 (M3a Task 10).</summary>
public class StampAndAttachmentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-stamp-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly AppFont _font = AppFont.Load(FontTestSupport.FontPath());
    private readonly AnnotationAssets _assets = new();
    private readonly PdfAppearanceSource _ap;
    private readonly PdfSharpAnnotationPatcher _patcher;

    private const double Scale = 2;

    public StampAndAttachmentTests()
    {
        Directory.CreateDirectory(_dir);
        _ap = new PdfAppearanceSource(_font, _assets);
        _patcher = new PdfSharpAnnotationPatcher(_font);
    }

    public void Dispose()
    {
        _t.Dispose();
        _patcher.Dispose();
        _font.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);
    private SaveService Svc() => new([], _ap, _patcher, _assets);
    private PaintContext Ctx => new(_font, _assets);

    private async Task<DocumentSession> OpenBlankAsync(string name = "blank.pdf")
    {
        TestPdfFactory.CreateBlank(P(name), 1, 200, 200);
        var s = await DocumentSession.OpenAsync(_t, P(name), null);
        await s.AnnotationsReady;
        return s;
    }

    private static Annotation Stamp(DocumentSession s, PdfRectF rect, StampSpec spec, Func<Annotation, Annotation>? f = null)
    {
        var a = new Annotation
        {
            Id = s.Annotations.NewId(), Kind = AnnotKind.Stamp, Rect = rect, Stamp = spec, Name = Guid.NewGuid().ToString("D"),
            Flags = PdfAnnotFlags.Print, Author = "tester", Subject = "stamp",
            CreatedAt = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero), ModifiedAt = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero),
        };
        return f is null ? a : f(a);
    }

    private static IReadOnlyList<OriginalAnnotation> ReadBack(string pdf)
        => AnnotationReader.Read(File.ReadAllBytes(pdf), null, [0])[0];

    private static T Deref<T>(PdfItem? item) where T : PdfItem => (T)((item as PdfReference)?.Value ?? item)!;

    private static PdfDictionary AnnotDict(PdfSharpDocument doc, int index)
        => Deref<PdfDictionary>(Deref<PdfArray>(doc.Pages[0].Elements["/Annots"]).Elements[index]);

    private static PdfDictionary ApResources(PdfDictionary annot)
        => Deref<PdfDictionary>(Deref<PdfDictionary>(Deref<PdfDictionary>(annot.Elements["/AP"]).Elements["/N"]).Elements["/Resources"]);

    private async Task<(byte[] Bgra, int W, int H)> RenderAsync(string pdf)
        => await _t.InvokeAsync(() =>
        {
            using var doc = Pdf.Native.PdfDocument.Open(_t, pdf);
            using var page = doc.LoadPage(0);
            var size = new PageTransform(page.BoundsPt, page.Rotation, Scale).ViewSize;
            int w = (int)Math.Ceiling(size.W), h = (int)Math.Ceiling(size.H);
            return (page.RenderTile(Scale, 0, 0, w, h, withAnnotations: true), w, h);
        });

    /// <summary>쪽 좌표(pt, 200pt 높이 쪽)의 픽셀 (R, G, B).</summary>
    private static (int R, int G, int B) At((byte[] Bgra, int W, int H) img, double xPt, double yPt)
    {
        int x = (int)Math.Round(xPt * Scale), y = (int)Math.Round((200 - yPt) * Scale);
        int o = (y * img.W + x) * 4;
        return (img.Bgra[o + 2], img.Bgra[o + 1], img.Bgra[o]);
    }

    private static bool IsGreen((int R, int G, int B) c) => c.G > 120 && c.R < 90 && c.B < 130 && c.G - c.R > 60;
    private static bool IsWhite((int R, int G, int B) c) => c.R > 235 && c.G > 235 && c.B > 235;

    // ---- 기본 스탬프 ----

    [Fact]
    public async Task BuiltinStamp_SaveReopen_RendersGreenBorderAndText()
    {
        await using var s = await OpenBlankAsync();
        var rect = new PdfRectF(30, 70, 170, 130);   // 140 x 60
        var a = Stamp(s, rect, new StampSpec(StampKind.Builtin, "Approved", "승인 Approved", null));
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a));
        string target = P("builtin.pdf");
        await Svc().SaveAsync(s, target);

        using (var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import))
        {
            var d = AnnotDict(doc, 0);
            Assert.Equal("/Stamp", d.Elements.GetName("/Subtype"));
            Assert.Equal("/Approved", d.Elements.GetName("/Name"));
            Assert.Equal(4, d.Elements.GetInteger("/F") & 4);
            Assert.Single(Deref<PdfDictionary>(ApResources(d).Elements["/Font"]).Elements);
        }

        var img = await RenderAsync(target);
        // 왼쪽 테두리(안쪽 1.25pt 선 중심) 가운데 높이에서 초록, 테두리 바깥은 흰색
        Assert.True(IsGreen(At(img, 31.25, 100)), "left border not green");
        Assert.True(IsWhite(At(img, 20, 100)), "outside the stamp not white");
        // 글자: 상자 안쪽 중앙 부근에 초록 픽셀이 있다(테두리를 피한 안쪽 영역)
        int greenInside = 0;
        for (double y = 80; y <= 120; y += 0.5)
            for (double x = 45; x <= 155; x += 0.5)
                if (IsGreen(At(img, x, y))) greenInside++;
        Assert.True(greenInside > 100, $"too few green text pixels inside the stamp: {greenInside}");
        // 가운데 위쪽 안쪽 여백(글자 위)은 흰색이다 (글자 크기 45%라 위아래에 여백)
        Assert.True(IsWhite(At(img, 100, 126)), "area above the text not white");

        // 다시 읽기: 원본 스탬프는 읽기 전용(OriginalAppearanceOnly), 화면은 PDFium
        var o = Assert.Single(ReadBack(target));
        Assert.Equal(ReadOnlyReason.OriginalAppearanceOnly, o.Reason);
        Assert.Null(o.Data);
    }

    [Fact]
    public void BuiltinStamp_SixColours()
    {
        foreach (var (name, hex) in new[]
        {
            ("Approved", "#1F9D55"), ("Reviewed", "#1F9D55"), ("Done", "#1F9D55"),
            ("Rejected", "#E5372D"), ("Confidential", "#8E44AD"), ("Draft", "#005FB8"),
        })
        {
            var a = new Annotation
            {
                Kind = AnnotKind.Stamp, Rect = new PdfRectF(0, 0, 140, 60), Color = new PdfColor(1, 2, 3),
                Stamp = new StampSpec(StampKind.Builtin, name, name, null),
            };
            var d = AnnotationPainter.Paint(a, 0, Ctx);
            Assert.Equal(PdfColor.FromHex(hex), d.Ops[0].Stroke);
            Assert.Equal(2.5f, d.Ops[0].StrokeWidth);
            Assert.Equal(PdfColor.FromHex(hex), d.Texts[0].Color);
        }
    }

    // ---- 글자 스탬프 ----

    [Fact]
    public async Task TextStamp_ShrinksToFit()
    {
        var rect = new PdfRectF(0, 0, 100, 40);
        PdfRectF Bounds(string text, out float size)
        {
            var a = new Annotation
            {
                Kind = AnnotKind.Stamp, Rect = rect, Color = new PdfColor(0, 0, 0),
                Stamp = new StampSpec(StampKind.Text, "", text, null),
            };
            var d = AnnotationPainter.Paint(a, 0, Ctx);
            size = d.Texts[0].SizePt;
            var gl = d.Texts[0].Glyphs;
            float right = gl.Max(g => g.Origin.X + g.Advance), left = gl.Min(g => g.Origin.X);
            return new PdfRectF(left, 0, right, 0);
        }

        Bounds("OK", out float shortSize);
        Assert.Equal(40f * 0.45f, shortSize, 3);          // 짧으면 높이의 45%

        var wide = Bounds("매우 긴 문구가 들어간 스탬프 VERY LONG STAMP TEXT", out float longSize);
        Assert.True(longSize < shortSize, $"long text not shrunk ({longSize})");
        // 줄여 맞춘 글자는 테두리 안쪽에 들어간다
        Assert.True(wide.Left >= rect.Left + 2.5f && wide.Right <= rect.Right - 2.5f, $"text outside the border: {wide.Left}..{wide.Right}");

        // 저장하면 /Name은 표준 이름이 아니라 우리 이름이다
        await using var s = await OpenBlankAsync();
        var a = Stamp(s, new PdfRectF(20, 80, 180, 120), new StampSpec(StampKind.Text, "", "검토 완료 REVIEWED", null), x => x with { Color = new PdfColor(200, 0, 0) });
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a));
        await Svc().SaveAsync(s, P("text.pdf"));
        using var doc = PdfReader.Open(P("text.pdf"), PdfDocumentOpenMode.Import);
        Assert.Equal("/SedolCustom", AnnotDict(doc, 0).Elements.GetName("/Name"));
    }

    // ---- 그림 스탬프 ----

    [Fact]
    public async Task ImageStamp_Png_WithAlpha_Saved()
    {
        string id = _assets.AddImage(TestImages.HalfTransparentRed());   // 40 x 20
        var rect = new PdfRectF(50, 50, 150, 150);                        // 100 x 100 정사각형 칸

        // Painter: 비율 2:1 유지, 가운데 (100 x 50)
        var probe = new Annotation { Kind = AnnotKind.Stamp, Rect = rect, Stamp = new StampSpec(StampKind.Image, "", null, id) };
        var drawing = AnnotationPainter.Paint(probe, 0, Ctx);
        var im = Assert.Single(drawing.Images);
        Assert.Equal(id, im.ImageId);
        Assert.Equal(100f, im.Rect.Width, 3);
        Assert.Equal(50f, im.Rect.Height, 3);
        Assert.Equal(100f, (im.Rect.Left + im.Rect.Right) / 2f, 3);
        Assert.Equal(100f, (im.Rect.Bottom + im.Rect.Top) / 2f, 3);

        await using var s = await OpenBlankAsync();
        var a = Stamp(s, rect, new StampSpec(StampKind.Image, "", null, id));
        var b = Stamp(s, new PdfRectF(10, 10, 60, 40), new StampSpec(StampKind.Image, "", null, id));   // 같은 이미지를 한 번 더
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a));
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, b));
        string target = P("png.pdf");

        // Review Focus 1: 세 번 저장해도 이미지 XObject는 겹쳐 생기지 않는다.
        for (int i = 0; i < 3; i++) await Svc().SaveAsync(s, target);

        using (var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(2, Deref<PdfArray>(doc.Pages[0].Elements["/Annots"]).Elements.Count);
            var d = AnnotDict(doc, 0);
            Assert.Equal("/SedolCustom", d.Elements.GetName("/Name"));
            var xobjs = Deref<PdfDictionary>(ApResources(d).Elements["/XObject"]);
            var image = Deref<PdfDictionary>(xobjs.Elements["/Im1"]);
            Assert.Equal("/Image", image.Elements.GetName("/Subtype"));
            Assert.Equal(40, image.Elements.GetInteger("/Width"));
            Assert.Equal(20, image.Elements.GetInteger("/Height"));
            Assert.Contains("FlateDecode", image.Elements["/Filter"]!.ToString());
            Assert.True(image.Elements.ContainsKey("/SMask"), "alpha must become an /SMask");
            // 두 번째 스탬프는 같은 XObject를 가리킨다
            var image2 = Deref<PdfDictionary>(Deref<PdfDictionary>(ApResources(AnnotDict(doc, 1)).Elements["/XObject"]).Elements["/Im1"]);
            Assert.Same(image, image2);

            // 파일 전체에서 SMask가 아닌 이미지 XObject는 하나, SMask는 하나
            int main = 0, masks = 0;
            foreach (var o in doc.Internals.GetAllObjects())
                if (o is PdfDictionary od && od.Elements.GetName("/Subtype") == "/Image")
                    if (od.Elements.ContainsKey("/SMask")) main++; else masks++;
            Assert.Equal(1, main);
            Assert.Equal(1, masks);
        }

        // 렌더: 왼쪽 반(불투명 빨강)은 빨강, 오른쪽 반(투명)은 흰색. 이미지 칸은 (50,75)-(150,125).
        var img = await RenderAsync(target);
        var red = At(img, 75, 100);
        Assert.True(red.R > 180 && red.G < 90 && red.B < 90, $"left half not red: {red}");
        Assert.True(IsWhite(At(img, 125, 100)), "transparent half not white");
        Assert.True(IsWhite(At(img, 75, 60)), "outside the fitted image (letterbox) not white");
    }

    [Fact]
    public async Task ImageStamp_Jpeg_KeptDct()
    {
        byte[] jpeg = TestImages.JpegHeaderOnly(3, 2);
        string id = _assets.AddImage(jpeg);
        await using var s = await OpenBlankAsync();
        var a = Stamp(s, new PdfRectF(40, 40, 160, 160), new StampSpec(StampKind.Image, "", null, id));
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a));
        string target = P("jpeg.pdf");
        await Svc().SaveAsync(s, target);

        using var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        var image = Deref<PdfDictionary>(Deref<PdfDictionary>(ApResources(AnnotDict(doc, 0)).Elements["/XObject"]).Elements["/Im1"]);
        Assert.Equal("/DCTDecode", image.Elements.GetName("/Filter"));
        Assert.Equal("/DeviceRGB", image.Elements.GetName("/ColorSpace"));
        Assert.Equal(3, image.Elements.GetInteger("/Width"));
        Assert.Equal(2, image.Elements.GetInteger("/Height"));
        Assert.Equal(8, image.Elements.GetInteger("/BitsPerComponent"));
        Assert.Equal(jpeg, image.Stream!.Value);   // 다시 인코딩하지 않고 원본 그대로
        Assert.False(image.Elements.ContainsKey("/SMask"));
    }

    // ---- 파일 첨부 ----

    [Fact]
    public async Task FileAttachment_Saved_EmbeddedBytesEqual()
    {
        var data = new byte[70_000];
        new Random(7).NextBytes(data);
        for (int i = 0; i < data.Length; i += 3) data[i] = (byte)'A';   // 압축이 되도록 일부 반복
        string id = _assets.AddFile("검토 메모.txt", data);

        await using var s = await OpenBlankAsync();
        var a = new Annotation
        {
            Id = s.Annotations.NewId(), Kind = AnnotKind.FileAttachment, Rect = new PdfRectF(90, 90, 110, 110), Color = new PdfColor(30, 90, 220),
            AttachmentId = id, AttachmentName = "검토 메모.txt", Name = Guid.NewGuid().ToString("D"), Flags = PdfAnnotFlags.Print,
            Author = "tester", CreatedAt = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero),
        };
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a));
        string target = P("attach.pdf");
        await Svc().SaveAsync(s, target);

        using (var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import))
        {
            var d = AnnotDict(doc, 0);
            Assert.Equal("/FileAttachment", d.Elements.GetName("/Subtype"));
            Assert.Equal("/Paperclip", d.Elements.GetName("/Name"));
            Assert.Equal(4, d.Elements.GetInteger("/F") & 4);
            var fs = Deref<PdfDictionary>(d.Elements["/FS"]);
            Assert.Equal("/Filespec", fs.Elements.GetName("/Type"));
            Assert.Equal("검토 메모.txt", fs.Elements.GetString("/UF"));
            var ef = Deref<PdfDictionary>(fs.Elements["/EF"]);
            var stream = Deref<PdfDictionary>(ef.Elements["/F"]);
            Assert.Equal("/EmbeddedFile", stream.Elements.GetName("/Type"));
            Assert.Equal(data.Length, Deref<PdfDictionary>(stream.Elements["/Params"]).Elements.GetInteger("/Size"));
        }

        // 원본으로 다시 읽으면 읽기 전용이고, 첨부 파일은 그대로 꺼낼 수 있다
        var o = Assert.Single(ReadBack(target));
        Assert.Equal(ReadOnlyReason.OriginalAppearanceOnly, o.Reason);
        var (name, bytes) = AnnotationReader.ReadAttachment(File.ReadAllBytes(target), null, 0, 0)!.Value;
        Assert.Equal("검토 메모.txt", name);
        Assert.Equal(data, bytes);
        Assert.Null(AnnotationReader.ReadAttachment(File.ReadAllBytes(target), null, 0, 5));

        // PDFium이 파일을 열고 클립 아이콘을 그린다(아이콘 줄 근처에 파란 픽셀)
        var img = await RenderAsync(target);
        int blue = 0;
        for (double y = 90; y <= 110; y += 0.5)
            for (double x = 90; x <= 110; x += 0.5)
            {
                var c = At(img, x, y);
                if (c.B > 150 && c.R < 120) blue++;
            }
        Assert.True(blue > 20, $"paperclip not drawn: {blue}");
    }

    [Fact]
    public void AttachmentOverLimit_Rejected()
    {
        var assets = new AnnotationAssets();
        Assert.Throws<ArgumentException>(() => assets.AddFile("big.bin", new byte[AnnotationAssets.MaxFileBytes + 1]));
        Assert.Equal(25 * 1024 * 1024, AnnotationAssets.MaxFileBytes);
    }

    // ---- 원본 스탬프 ----

    [Fact]
    public async Task OriginalStamp_ReadOnly_DrawnByPdfium()
    {
        // 우리 파이프라인으로 스탬프가 든 파일을 만든 뒤, 새 세션에서 "원본 스탬프"로 연다.
        string src = P("with_stamp.pdf");
        await using (var s = await OpenBlankAsync("seed.pdf"))
        {
            var a = Stamp(s, new PdfRectF(30, 70, 170, 130), new StampSpec(StampKind.Builtin, "Rejected", "반려 Rejected", null));
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a));
            await Svc().SaveAsync(s, src);
        }

        var o = Assert.Single(ReadBack(src));
        Assert.Equal(ReadOnlyReason.OriginalAppearanceOnly, o.Reason);
        Assert.Null(o.Data);
        Assert.Equal(PdfAnnotSubtype.Stamp, o.Subtype);

        // 열어서 아무것도 건드리지 않고 다른 이름으로 저장해도 원본 스탬프는 그대로 남아 PDFium이 그린다
        string copy = P("copy.pdf");
        await using (var s2 = await DocumentSession.OpenAsync(_t, src, null))
        {
            await s2.AnnotationsReady;
            await Svc().SaveAsync(s2, copy);
        }
        var img = await RenderAsync(copy);
        var c = At(img, 31.25, 100);
        Assert.True(c.R > 180 && c.G < 90 && c.B < 90, $"original stamp border not drawn by PDFium: {c}");   // 반려 = 빨강 #E5372D
        var o2 = Assert.Single(ReadBack(copy));
        Assert.Equal(ReadOnlyReason.OriginalAppearanceOnly, o2.Reason);
    }
}
