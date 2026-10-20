using System.Text;
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

/// <summary>텍스트 상자·타자기·설명선(FreeText): Painter, 패치 계획, PDFsharp, 다시 읽기.</summary>
public class FreeTextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-freetext-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly AppFont _font = AppFont.Load(FontTestSupport.FontPath());
    private readonly PdfAppearanceSource _ap;
    private readonly PdfSharpAnnotationPatcher _patcher;

    public FreeTextTests()
    {
        Directory.CreateDirectory(_dir);
        _ap = new PdfAppearanceSource(_font);
        _patcher = new PdfSharpAnnotationPatcher(_font);
    }

    public void Dispose()
    {
        _t.Dispose();
        _patcher.Dispose();
        _font.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static readonly PdfColor Red = new(229, 55, 45);
    private static readonly PdfColor Yellow = new(255, 245, 157);
    private static readonly PdfColor Blue = new(30, 90, 220);
    private PaintContext Ctx => new(_font, AnnotationAssets.Empty);

    private static Annotation Box(DocumentSession s, PdfRectF rect, string text, Func<Annotation, Annotation>? f = null)
    {
        var a = new Annotation
        {
            Id = s.Annotations.NewId(), Kind = AnnotKind.FreeText, Intent = FreeTextIntent.TextBox, Rect = rect,
            Contents = text, FontSizePt = 12f, TextColor = Blue, Color = Red, FillColor = Yellow, BorderWidth = 1.5f,
            Name = Guid.NewGuid().ToString("D"), Flags = PdfAnnotFlags.Print, Author = "tester", Subject = "textbox",
            CreatedAt = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero), ModifiedAt = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero),
        };
        return f is null ? a : f(a);
    }

    private SaveService Svc() => new([], _ap, _patcher);

    private async Task<DocumentSession> OpenBlankAsync(string name = "blank.pdf")
    {
        string p = Path.Combine(_dir, name);
        TestPdfFactory.CreateBlank(p, 1, 200, 200);
        var s = await DocumentSession.OpenAsync(_t, p, null);
        await s.AnnotationsReady;
        return s;
    }

    private static IReadOnlyList<OriginalAnnotation> ReadBack(string pdf)
        => AnnotationReader.Read(File.ReadAllBytes(pdf), null, [0])[0];

    [Fact]
    public async Task TextBox_KoreanWrapped_SaveReopen_ReadBackEquivalent()
    {
        const string text = "가나다 도면 검토 ABC 가나다라마바사아자차카타파하";
        await using var s = await OpenBlankAsync();
        var a = Box(s, new PdfRectF(20, 100, 120, 180), text, x => x with { Align = TextAlign.Center });

        // 상자 폭 100pt에 안 들어가므로 여러 줄로 나뉘고 모두 상자 안에 있다.
        var laid = TextLayout.Layout(_font, text, a.FontSizePt, a.Rect.Width - 4, a.Align, new PdfPointF(a.Rect.Left + 2, a.Rect.Top - 2));
        Assert.True(laid.Lines.Length >= 2);
        Assert.True(laid.Bounds.Left >= a.Rect.Left && laid.Bounds.Right <= a.Rect.Right && laid.Bounds.Bottom >= a.Rect.Bottom);

        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a));
        string target = Path.Combine(_dir, "out.pdf");
        await Svc().SaveAsync(s, target);

        var o = Assert.Single(ReadBack(target));
        Assert.Equal(ReadOnlyReason.None, o.Reason);
        var r = o.Data!;
        Assert.Equal(AnnotKind.FreeText, r.Kind);
        Assert.Equal(FreeTextIntent.TextBox, r.Intent);
        Assert.Equal(text, r.Contents);
        Assert.Equal(12f, r.FontSizePt);
        Assert.Equal(Blue, r.TextColor);
        Assert.Equal(TextAlign.Center, r.Align);
        Assert.Equal(Red, r.Color);
        Assert.Equal(Yellow, r.FillColor);
        Assert.Equal(1.5f, r.BorderWidth);
        Assert.Equal("tester", r.Author);
        Assert.Equal("textbox", r.Subject);
        Assert.Null(r.TextBox);
        Assert.True(r.Vertices.IsEmpty);
        Assert.Equal(a.Rect.Left, r.Rect.Left, 0.01);
        Assert.Equal(a.Rect.Top, r.Rect.Top, 0.01);
        Assert.Equal(a.Rect.Right, r.Rect.Right, 0.01);
        Assert.Equal(a.Rect.Bottom, r.Rect.Bottom, 0.01);

        // AP: 글꼴은 F1 하나, 인쇄 플래그 켜짐
        using var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        var dict = AnnotDict(doc, 0);
        Assert.Equal(4, dict.Elements.GetInteger("/F") & 4);
        Assert.Single(FontsOfAp(dict).Elements);
    }

    [Fact]
    public async Task Typewriter_RectFitsText()
    {
        await using var s = await OpenBlankAsync();
        var t = Box(s, new PdfRectF(30, 100, 190, 190), "가나다 ABC\n두 번째 줄 입니다", x => x with
        {
            Intent = FreeTextIntent.Typewriter, FillColor = null, Color = null, BorderWidth = 0, FontSizePt = 14f,
        });
        var fit = AnnotationPainter.FitTypewriter(t, Ctx);

        var laid = TextLayout.Layout(_font, t.Contents!, 14f, float.PositiveInfinity, TextAlign.Left, new PdfPointF(32, 188));
        Assert.Equal(2, laid.Lines.Length);
        Assert.Equal(t.Rect.Left, fit.Rect.Left);
        Assert.Equal(t.Rect.Top, fit.Rect.Top);
        Assert.Equal(laid.Bounds.Width + 4, fit.Rect.Width, 0.01);
        Assert.Equal(2 * 14f * 1.2f + 4, fit.Rect.Height, 0.01);

        // 테두리·배경이 없고, 글자는 맞춘 상자 안에 있다.
        var d = AnnotationPainter.Paint(fit, 0, Ctx);
        Assert.Empty(d.Ops);
        var run = Assert.Single(d.Texts);
        Assert.All(run.Glyphs, g => Assert.True(
            g.Origin.X >= fit.Rect.Left && g.Origin.X <= fit.Rect.Right && g.Origin.Y >= fit.Rect.Bottom && g.Origin.Y <= fit.Rect.Top));
        Assert.True(d.Bounds.Left >= fit.Rect.Left - 0.01 && d.Bounds.Right <= fit.Rect.Right + 0.01 && d.Bounds.Bottom >= fit.Rect.Bottom - 0.01);

        // 자동 줄바꿈 없음: 아주 긴 한 줄도 한 줄이다.
        var longLine = AnnotationPainter.FitTypewriter(t with { Contents = new string('가', 60) }, Ctx);
        Assert.True(longLine.Rect.Width > 14f * 60 * 0.8f, $"width {longLine.Rect.Width}");
        Assert.Equal(14f * 1.2f + 4, longLine.Rect.Height, 0.01);

        // 저장하면 /IT 가 FreeTextTypeWriter 이고 읽어 오면 다시 타자기다.
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, fit));
        string target = Path.Combine(_dir, "tw.pdf");
        await Svc().SaveAsync(s, target);
        using (var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import))
            Assert.Equal("/FreeTextTypeWriter", AnnotDict(doc, 0).Elements.GetName("/IT"));
        var r = Assert.Single(ReadBack(target)).Data!;
        Assert.Equal(FreeTextIntent.Typewriter, r.Intent);
        Assert.Equal(14f, r.FontSizePt);
        Assert.Null(r.Color);
    }

    [Fact]
    public async Task Callout_LeaderAndRd_Saved()
    {
        await using var s = await OpenBlankAsync();
        var box = new PdfRectF(90, 120, 190, 180);
        PdfPointF[] cl = [new(20, 30), new(60, 90), new(90, 140)];
        var a = Box(s, box, "도면 검토 필요", x => x with
        {
            Intent = FreeTextIntent.Callout, Vertices = [.. cl], TextBox = box, EndEnding = LineEnding.OpenArrow,
        });
        a = a with { Rect = AnnotationGeometry.ComputeRect(a) };
        Assert.True(a.Rect.Left < box.Left && a.Rect.Bottom < box.Bottom);

        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a));
        string target = Path.Combine(_dir, "co.pdf");
        await Svc().SaveAsync(s, target);

        using (var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import))
        {
            var d = AnnotDict(doc, 0);
            Assert.Equal("/FreeTextCallout", d.Elements.GetName("/IT"));
            Assert.Equal(cl.SelectMany(p => new[] { p.X, p.Y }).Select(v => (double)v), Nums(d, "/CL"), new Tol());
            double[] rd = [box.Left - a.Rect.Left, box.Bottom - a.Rect.Bottom, a.Rect.Right - box.Right, a.Rect.Top - box.Top];
            Assert.Equal(rd, Nums(d, "/RD"), new Tol());
            Assert.Equal("/OpenArrow", d.Elements.GetName("/LE"));
        }

        var r = Assert.Single(ReadBack(target)).Data!;
        Assert.Equal(FreeTextIntent.Callout, r.Intent);
        Assert.Equal(cl.Length, r.Vertices.Length);
        for (int i = 0; i < cl.Length; i++) { Assert.Equal(cl[i].X, r.Vertices[i].X, 0.01); Assert.Equal(cl[i].Y, r.Vertices[i].Y, 0.01); }
        Assert.Equal(LineEnding.OpenArrow, r.EndEnding);
        Assert.NotNull(r.TextBox);
        Assert.Equal(box.Left, r.TextBox!.Value.Left, 0.01);
        Assert.Equal(box.Top, r.TextBox!.Value.Top, 0.01);
        Assert.Equal(a.Rect.Left, r.Rect.Left, 0.01);
        Assert.Equal("도면 검토 필요", r.Contents);
    }

    [Fact]
    public async Task FreeText_SaveThreeTimes_OneFontPerAp()
    {
        await using var s = await OpenBlankAsync();
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, Box(s, new PdfRectF(20, 100, 180, 180), "가나다 도면 검토 ABC")));
        string target = Path.Combine(_dir, "three.pdf");
        for (int i = 0; i < 3; i++)
        {
            await Svc().SaveAsync(s, target);
            using var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import);
            var annots = Deref<PdfArray>(doc.Pages[0].Elements["/Annots"]);
            Assert.Single(annots.Elements);   // 저장할 때마다 FreeText가 겹쳐 생기지 않는다
            Assert.Single(FontsOfAp(AnnotDict(doc, 0)).Elements);
            int type0 = doc.Internals.GetAllObjects().OfType<PdfDictionary>().Count(d => d.Elements.GetName("/Subtype") == "/Type0");
            Assert.Equal(1, type0);   // 글꼴 객체도 하나뿐 (서브셋이 쌓이지 않는다)
        }
    }

    [Fact]
    public async Task FreeText_TextCopiedFromPdfium_EqualsContents()
    {
        const string text = "가나다 도면 검토 ABC";
        await using var s = await OpenBlankAsync();
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, Box(s, new PdfRectF(10, 100, 190, 140), text)));
        string saved = Path.Combine(_dir, "t.pdf");
        await Svc().SaveAsync(s, saved);

        // PDFium의 쪽 글자 추출은 주석 AP를 보지 않으므로, 저장 파일의 AP 폼을 쪽 내용으로 옮겨 ToUnicode 복사 결과를 본다.
        string moved = Path.Combine(_dir, "t-moved.pdf");
        using (var doc = PdfReader.Open(saved, PdfDocumentOpenMode.Modify))
        {
            var page = doc.Pages[0];
            var form = Deref<PdfDictionary>(Deref<PdfDictionary>(AnnotDict(doc, 0).Elements["/AP"]).Elements["/N"]);
            var xo = new PdfDictionary(doc);
            xo.Elements["/Fm0"] = form.Reference!;
            var res = new PdfDictionary(doc);
            res.Elements["/XObject"] = xo;
            page.Elements["/Resources"] = res;
            var cont = new PdfDictionary(doc);
            cont.CreateStream(Encoding.ASCII.GetBytes("/Fm0 Do"));
            doc.Internals.AddObject(cont);
            page.Elements["/Contents"] = cont.Reference!;
            page.Elements.Remove("/Annots");
            doc.Save(moved);
        }
        string got = await _t.InvokeAsync(() =>
        {
            using var d = Pdf.Native.PdfDocument.Open(_t, moved);
            using var p = d.LoadPage(0);
            using var tp = p.LoadText();
            return tp.GetText(0, tp.CharCount);
        });
        Assert.Equal(text, got.Trim());
    }

    [Fact]
    public void OriginalFreeTextFromOtherApp_ReadDaSizeAndColor()
    {
        string pdf = MakeOtherAppFreeText("0 0.5 1 rg /Helv 14 Tf", q: 2, extra: d => d.Elements["/Foo"] = new PdfName("/Bar"));
        var o = Assert.Single(ReadBack(pdf));
        Assert.Equal(ReadOnlyReason.None, o.Reason);
        var r = o.Data!;
        Assert.Equal(AnnotKind.FreeText, r.Kind);
        Assert.Equal(14f, r.FontSizePt);
        Assert.Equal(new PdfColor(0, 128, 255), r.TextColor);
        Assert.Equal(TextAlign.Right, r.Align);
        Assert.Equal(FreeTextIntent.TextBox, r.Intent);
        Assert.Equal("Hello", r.Contents);

        // 회색 g, 글자 크기 0(자동)
        var r2 = Assert.Single(ReadBack(MakeOtherAppFreeText("0.5 g /Helv 0 Tf", q: 0))).Data!;
        Assert.Equal(new PdfColor(128, 128, 128), r2.TextColor);
        Assert.Equal(12f, r2.FontSizePt);

        // /IT 가 세 가지 밖이면 읽기 전용
        var o3 = Assert.Single(ReadBack(MakeOtherAppFreeText("0 g /Helv 12 Tf", q: 0, extra: d => d.Elements["/IT"] = new PdfName("/FreeText"))));
        Assert.Equal(ReadOnlyReason.UnsupportedType, o3.Reason);
        // 설명선인데 /CL 이 없으면 망가진 것
        var o4 = Assert.Single(ReadBack(MakeOtherAppFreeText("0 g /Helv 12 Tf", q: 0, extra: d => d.Elements["/IT"] = new PdfName("/FreeTextCallout"))));
        Assert.Equal(ReadOnlyReason.Malformed, o4.Reason);
    }

    [Fact]
    public async Task OriginalFreeText_Touched_PatchedInPlace_KeepsUnknownKeys()
    {
        string pdf = MakeOtherAppFreeText("0 0.5 1 rg /Helv 14 Tf", q: 0, extra: d => d.Elements["/Foo"] = new PdfName("/Bar"));
        await using var s = await DocumentSession.OpenAsync(_t, pdf, null);
        await s.AnnotationsReady;
        var id = s.Pages[0].Id;
        var before = Assert.Single(s.Annotations.Get(id).Items).Data;
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(id, [(before, before with { Contents = "수정된 글 ABC" })]));

        string target = Path.Combine(_dir, "touched.pdf");
        await Svc().SaveAsync(s, target);
        using (var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import))
        {
            Assert.Single(Deref<PdfArray>(doc.Pages[0].Elements["/Annots"]).Elements);
            var d = AnnotDict(doc, 0);
            Assert.Equal("/Bar", d.Elements.GetName("/Foo"));   // 모르는 키 보존
            Assert.Single(FontsOfAp(d).Elements);
        }
        var r = Assert.Single(ReadBack(target)).Data!;
        Assert.Equal("수정된 글 ABC", r.Contents);
        Assert.Equal(14f, r.FontSizePt);
        Assert.Equal(new PdfColor(0, 128, 255), r.TextColor);
    }

    [Fact]
    public void OriginalFreeTextCallout_LeArrayOrName_BothRead()
    {
        foreach (var le in new PdfItem[] { new PdfName("/OpenArrow"), null! })
        {
            string pdf = MakeOtherAppFreeText("0 g /Helv 12 Tf", q: 0, extra: d =>
            {
                d.Elements["/IT"] = new PdfName("/FreeTextCallout");
                d.Elements["/CL"] = Rect(d.Owner, 10, 10, 40, 60);
                d.Elements["/RD"] = Rect(d.Owner, 0, 0, 0, 0);
                d.Elements["/LE"] = le ?? new PdfArray(d.Owner, new PdfName("/OpenArrow"), new PdfName("/None"));
            });
            var o = Assert.Single(ReadBack(pdf));
            Assert.Equal(ReadOnlyReason.None, o.Reason);
            Assert.Equal(FreeTextIntent.Callout, o.Data!.Intent);
            Assert.Equal(LineEnding.OpenArrow, o.Data.EndEnding);
        }
    }

    [Fact]
    public async Task OriginalFreeText_Touched_StaleCaRemoved()
    {
        string pdf = MakeOtherAppFreeText("0 g /Helv 12 Tf", q: 0, extra: d => d.Elements["/CA"] = new PdfReal(0.4));
        await using var s = await DocumentSession.OpenAsync(_t, pdf, null);
        await s.AnnotationsReady;
        var id = s.Pages[0].Id;
        var before = Assert.Single(s.Annotations.Get(id).Items).Data;
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(id, [(before, before with { Contents = "touched" })]));
        string target = Path.Combine(_dir, "ca.pdf");
        await Svc().SaveAsync(s, target);
        using var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        Assert.False(AnnotDict(doc, 0).Elements.ContainsKey("/CA"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Callout_WithUnsupportedVertexCount_FallsBackToTextBox(int count)
    {
        await using var s = await OpenBlankAsync();
        var box = new PdfRectF(90, 120, 190, 180);
        var a = Box(s, box, "leader", x => x with
        {
            Intent = FreeTextIntent.Callout, TextBox = box,
            Vertices = [.. Enumerable.Range(0, count).Select(i => new PdfPointF(10 + i * 15, 20 + i * 20))],
        });
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a));
        string target = Path.Combine(_dir, $"co{count}.pdf");
        await Svc().SaveAsync(s, target);
        using (var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import))
        {
            var d = AnnotDict(doc, 0);
            Assert.False(d.Elements.ContainsKey("/IT"));
            Assert.False(d.Elements.ContainsKey("/CL"));
        }
        var r = Assert.Single(ReadBack(target));
        Assert.Equal(ReadOnlyReason.None, r.Reason);
        Assert.Equal(FreeTextIntent.TextBox, r.Data!.Intent);
    }

    [Fact]
    public async Task FreeText_DeleteNewAfterSave_ThenSaveAgain_Gone()
    {
        await using var s = await OpenBlankAsync();
        var a = Box(s, new PdfRectF(20, 100, 120, 150), "first");
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a));
        string t1 = Path.Combine(_dir, "d1.pdf"), t2 = Path.Combine(_dir, "d2.pdf");
        await Svc().SaveAsync(s, t1);
        Assert.Single(ReadBack(t1));

        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Delete(s.Pages[0].Id, [a]));
        await Svc().SaveAsync(s, t2);
        Assert.Empty(ReadBack(t2));
        using var doc = PdfReader.Open(t2, PdfDocumentOpenMode.Import);
        Assert.True(!doc.Pages[0].Elements.ContainsKey("/Annots") || Deref<PdfArray>(doc.Pages[0].Elements["/Annots"]).Elements.Count == 0);
    }

    [Fact]
    public async Task FreeText_UndoOfAdd_ThenSave_NoAnnotation()
    {
        await using var s = await OpenBlankAsync();
        var a = Box(s, new PdfRectF(20, 100, 120, 150), "undo me");
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a));
        await s.Commands.UndoAsync();
        string target = Path.Combine(_dir, "u.pdf");
        await Svc().SaveAsync(s, target);
        Assert.Empty(ReadBack(target));
    }

    [Fact]
    public void Paint_FreeTextWithoutContext_ThrowsInvalidOperation()
    {
        var a = new Annotation { Kind = AnnotKind.FreeText, Rect = new PdfRectF(0, 0, 50, 50), Contents = "x" };
        Assert.Throws<InvalidOperationException>(() => AnnotationPainter.Paint(a, 0));
    }

    // ---- 도우미 ----

    private string MakeOtherAppFreeText(string da, int q, Action<PdfDictionary>? extra = null)
    {
        string p = Path.Combine(_dir, $"other-{Guid.NewGuid():N}.pdf");
        TestPdfFactory.CreateBlank(p, 1, 200, 200);
        using (var doc = PdfReader.Open(p, PdfDocumentOpenMode.Modify))
        {
            var d = new PdfDictionary(doc);
            d.Elements["/Type"] = new PdfName("/Annot");
            d.Elements["/Subtype"] = new PdfName("/FreeText");
            d.Elements["/Rect"] = Rect(doc, 20, 100, 120, 150);
            d.Elements["/Contents"] = new PdfString("Hello");
            d.Elements["/DA"] = new PdfString(da);
            d.Elements["/Q"] = new PdfInteger(q);
            d.Elements["/F"] = new PdfInteger(4);
            d.Elements["/NM"] = new PdfString("other-1");
            extra?.Invoke(d);
            doc.Internals.AddObject(d);
            var arr = new PdfArray(doc);
            arr.Elements.Add(d.Reference!);
            doc.Pages[0].Elements["/Annots"] = arr;
            doc.Save(p);
        }
        return p;
    }

    private static PdfArray Rect(PdfSharpDocument doc, params double[] v)
    {
        var a = new PdfArray(doc);
        foreach (var x in v) a.Elements.Add(new PdfReal(x));
        return a;
    }

    private static T Deref<T>(PdfItem? item) where T : PdfItem => (T)((item as PdfReference)?.Value ?? item)!;

    [Fact]
    public async Task Typewriter_Rotated_FitsAlongScreenLine_TopLeftFixed()
    {
        await using var s = await OpenBlankAsync("tw-rot.pdf");
        var t = Box(s, new PdfRectF(50, 60, 70, 78.4f), "가나다라마바사", x => x with
        {
            Intent = FreeTextIntent.Typewriter, Rotation = 90, Color = null, FillColor = null, BorderWidth = 0, FontSizePt = 12,
        });
        var fit = AnnotationPainter.FitTypewriter(t, Ctx);
        // 반시계 90도: 화면에서 본 왼쪽 위 = 쪽 좌표의 (Left, Bottom). 글줄은 쪽 좌표 위쪽(y)으로 자란다.
        Assert.Equal(50f, fit.Rect.Left, 0.01);
        Assert.Equal(60f, fit.Rect.Bottom, 0.01);
        Assert.Equal(12f * 1.2f + 4, fit.Rect.Width, 0.01);
        Assert.True(fit.Rect.Height > 12f * 7 * 0.8f, $"height {fit.Rect.Height}");
        var d = AnnotationPainter.Paint(fit, 0, Ctx);
        Assert.NotNull(d.Transform);
        Assert.True(d.Bounds.Left >= fit.Rect.Left - 0.01 && d.Bounds.Right <= fit.Rect.Right + 0.01
            && d.Bounds.Bottom >= fit.Rect.Bottom - 0.01 && d.Bounds.Top <= fit.Rect.Top + 0.01, $"bounds {d.Bounds} in {fit.Rect}");
        Assert.Contains(" cm\n", PdfContentWriter.Write(d));
    }

    /// <summary>2026-10-20 버그: 다른 편집기는 FreeText의 /C를 배경으로 읽는다 → /C = 배경, 테두리 색은 /DA의 RG, /IC 없음.</summary>
    [Fact]
    public async Task TextBox_SavedKeys_CIsBackground_BorderInDa()
    {
        await using var s = await OpenBlankAsync("keys.pdf");
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, Box(s, new PdfRectF(20, 100, 120, 180), "abc")));
        string target = Path.Combine(_dir, "keys-out.pdf");
        await Svc().SaveAsync(s, target);
        using (var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import))
        {
            var d = AnnotDict(doc, 0);
            static double V(PdfItem i) => i is PdfReal r ? r.Value : ((PdfInteger)i).Value;
            var c = d.Elements.GetArray("/C")!;
            Assert.Equal([255 / 255.0, 245 / 255.0, 157 / 255.0], c.Elements.Select(V).ToArray(), new Tol());
            Assert.False(d.Elements.ContainsKey("/IC"));
            Assert.Contains("0.898 0.216 0.176 RG", d.Elements.GetString("/DA"));
        }
        var r = Assert.Single(ReadBack(target)).Data!;
        Assert.Equal(Red, r.Color);
        Assert.Equal(Yellow, r.FillColor);
    }

    /// <summary>2026-10-20 버그: 회전된 쪽(/Rotate 90)에서 텍스트 상자 글자가 세로로 써짐 → Rotation만큼 돌려 화면에서 가로로 읽힌다.</summary>
    [Fact]
    public async Task TextBox_OnRotatedPage_TextReadsHorizontally()
    {
        string p = Path.Combine(_dir, "rot.pdf");
        TestPdfFactory.CreateBlank(p, 1, 200, 300);
        using (var d = PdfReader.Open(p, PdfDocumentOpenMode.Modify)) { d.Pages[0].Rotate = 90; d.Save(p); }
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        await s.AnnotationsReady;
        // 쪽 좌표로 세로로 긴 상자(40 × 160) = 화면에서 가로로 긴 상자
        var a = Box(s, new PdfRectF(80, 70, 120, 230), "WWWWWWWWWWWWWWWW",
            x => x with { Rotation = 90, FillColor = null, Color = null, BorderWidth = 0, FontSizePt = 10 });
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, a));
        string target = Path.Combine(_dir, "rot-out.pdf");
        await Svc().SaveAsync(s, target);

        var (w, h, px) = await _t.InvokeAsync(() =>
        {
            using var doc = Pdf.Native.PdfDocument.Open(_t, target);
            using var page = doc.LoadPage(0);
            var size = page.SizePt;
            int pw = (int)size.Width, ph = (int)size.Height;
            return (pw, ph, page.RenderTile(1, 0, 0, pw, ph, true));
        });
        Assert.True(w > h, "the page is shown in landscape");
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[(y * w + x) * 4] + px[(y * w + x) * 4 + 1] + px[(y * w + x) * 4 + 2] < 600) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        Assert.True(maxX >= 0, "text was drawn");
        Assert.True(maxX - minX > 5 * (maxY - minY), $"ink {maxX - minX} x {maxY - minY}: text runs left to right on screen");

        using (var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import))
            Assert.Equal(90, AnnotDict(doc, 0).Elements.GetInteger("/Rotate"));
        Assert.Equal(90, Assert.Single(ReadBack(target)).Data!.Rotation);
    }

    private static PdfDictionary AnnotDict(PdfSharpDocument doc, int index)
        => Deref<PdfDictionary>(Deref<PdfArray>(doc.Pages[0].Elements["/Annots"]).Elements[index]);

    private static PdfDictionary FontsOfAp(PdfDictionary annot)
    {
        var form = Deref<PdfDictionary>(Deref<PdfDictionary>(annot.Elements["/AP"]).Elements["/N"]);
        return Deref<PdfDictionary>(Deref<PdfDictionary>(form.Elements["/Resources"]).Elements["/Font"]);
    }

    private static double[] Nums(PdfDictionary d, string key)
        => Deref<PdfArray>(d.Elements[key]).Elements.Select(i => i switch { PdfInteger n => (double)n.Value, PdfReal r => r.Value, _ => double.NaN }).ToArray();

    private sealed class Tol : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 0.01;
        public int GetHashCode(double v) => 0;
    }
}
