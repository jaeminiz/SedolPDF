using System.Text;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.ContentStream;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.Document.Content;
using PdfEditor.Document.Output;
using PdfEditor.Document.Saving;
using PdfEditor.TextEdit.Paragraphs;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfEditor.TextEdit.Tests.Paragraphs;

/// <summary>M5 Task 6: 문단 편집의 저장 — 직접 인코딩 덧붙임(원래 글꼴)·지운 글 완전 제거·경로 선택(내용 스트림 직접 쓰기 → PDFium 경로 → 저장 거절).</summary>
public sealed class ParagraphSaveTests : IDisposable
{
    private static readonly Lazy<AppFont> Font = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        return AppFont.Load(Path.Combine(dir!.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"));
    });

    private readonly PdfiumThread _t = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-m5task6-" + Guid.NewGuid().ToString("N"));

    public ParagraphSaveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>probe가 모든 쪽을 거절하는 패처(객체 번호 대응 거절 흉내) — 적용은 진짜 패처가 한다.</summary>
    private sealed class RejectingProbe(IAnnotationPatcher inner, string reason) : IAnnotationPatcher, IContentRewriteProbe
    {
        public void Apply(string pdfPath, string? password, PatchPlan plan, AnnotationAssets assets) => inner.Apply(pdfPath, password, plan, assets);
        public IReadOnlyDictionary<int, string> CheckContentRewrites(string pdfPath, string? password, IReadOnlyList<PageContentRewrite> pages, AnnotationAssets assets) =>
            pages.ToDictionary(p => p.PageIndex, _ => reason);
    }

    private async Task<(DocumentSession S, PageId Page, ParagraphModel M)> OpenParagraph(string sample = "doc-en-left", string? path = null)
    {
        var s = await DocumentSession.OpenAsync(_t, path ?? ParagraphTestData.Sample(sample).Path, null);
        var page = s.Pages[0].Id;
        var m = await _t.InvokeAsync(() =>
        {
            var idx = s.GetObjectIndex(page);
            foreach (var e in idx.Entries.Where(e => e.Type == PdfPageObjectType.Text && !e.Ref.IsInserted).ToList())
                if (ParagraphReader.Detect(s, page, e.Ref, idx).Model is { IsParagraph: true } mm && mm.Lines.Length >= 3) return mm;
            return null;
        });
        Assert.NotNull(m);
        return (s, page, m!);
    }

    /// <summary>가운데 줄의 낱말 하나를 글자 순서만 뒤집은 글(같은 길이).</summary>
    private static (string Edited, int Line) ReverseWord(ParagraphModel m)
    {
        string orig = ParagraphLayout.OriginalText(m);
        for (int li = 1; li < m.Lines.Length - 1; li++)
            foreach (var w in m.Lines[li].Text.Split(' '))
            {
                if (w.Length < 4 || w.Distinct().Count() <= 2 || !w.All(char.IsLetter)) continue;
                string rev = new(w.Reverse().ToArray());
                int at = orig.IndexOf(w, StringComparison.Ordinal);
                int lineStart = orig.IndexOf(m.Lines[li].Text, StringComparison.Ordinal);
                if (rev == w || lineStart < 0 || at != lineStart + m.Lines[li].Text.IndexOf(w, StringComparison.Ordinal)) continue;
                return (orig[..at] + rev + orig[(at + w.Length)..], li);
            }
        throw new InvalidOperationException("no reversible word");
    }

    private async Task<ParagraphPlan> Edit(DocumentSession s, PageId page, ParagraphModel m, string text)
    {
        var plan = await _t.InvokeAsync(() => ParagraphEditPlanner.Plan(s, page, m, text, Font.Value));
        Assert.True(plan.Ok, plan.Refusal.ToString());
        Assert.NotNull(plan.Edit);
        await s.Commands.ExecuteAsync(plan.ToCommand(page)!);
        return plan;
    }

    private SaveService Svc(DocumentSession s, IAnnotationPatcher? patcher = null) => new([], null, patcher ?? new PdfSharpAnnotationPatcher(Font.Value), s.Assets);

    private string Out(string name) => Path.Combine(_dir, name);

    private Task<string> SavedText(string pdf) => _t.InvokeAsync(() =>
    {
        using var d = Pdf.Native.PdfDocument.Open(_t, pdf, null);
        using var p = d.LoadPage(0);
        using var tp = p.LoadText();
        return tp.GetText(0, tp.CharCount);
    });

    private static string Squash(string t) => string.Concat(t.Where(c => !char.IsWhiteSpace(c)));

    private static byte[] PageContentBytes(string pdf)
    {
        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        var loaded = PageContent.Load(doc.Pages[0]);
        Assert.NotNull(loaded.Data);
        return loaded.Data!;
    }

    private static int FontObjectCount(string pdf)
    {
        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        return doc.Internals.GetAllObjects().Count(o => o is PdfDictionary d && d.Elements.GetName("/Type") == "/Font");
    }

    private static List<string> PageFontNames(string pdf)
    {
        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        var fonts = doc.Pages[0].Resources.Elements.GetDictionary("/Font");
        return fonts is null ? [] : fonts.Elements.Keys.ToList();
    }

    private Task<byte[]> Render(string pdf, int w = 595, int h = 842) => _t.InvokeAsync(() =>
    {
        using var d = Pdf.Native.PdfDocument.Open(_t, pdf, null);
        using var p = d.LoadPage(0);
        return p.RenderTile(1.0, 0, 0, w, h, false);
    });

    /// <summary>두 그림이 다른 픽셀 수(채널 8 초과). skip 안(쪽 공간 사각형, 아래가 원점)은 센 적이 없다.</summary>
    private static int Diff(byte[] a, byte[] b, int w, int h, PdfRectF? skip, float margin = 6)
    {
        int diff = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (skip is { } r && x >= r.Left - margin && x <= r.Right + margin && h - y >= r.Bottom - margin && h - y <= r.Top + margin) continue;
                int i = (y * w + x) * 4;
                if (Math.Abs(a[i] - b[i]) > 8 || Math.Abs(a[i + 1] - b[i + 1]) > 8 || Math.Abs(a[i + 2] - b[i + 2]) > 8) diff++;
            }
        return diff;
    }

    /// <summary>doc-en-left 쪽 오른쪽 아래에 셰이딩(sh)을 더한 PDF(PDFium 다시 쓰기가 못 쓰는 알려진 한계 — M4x).</summary>
    private string WithShading(string src, string name, bool pattern = false)
    {
        string dst = Out(name);
        using var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify);
        var page = doc.Pages[0];
        PdfArray Arr(params double[] v) { var a = new PdfArray(doc); foreach (var x in v) a.Elements.Add(new PdfReal(x)); return a; }
        var fn = new PdfDictionary(doc);
        fn.Elements["/FunctionType"] = new PdfInteger(2);
        fn.Elements["/Domain"] = Arr(0, 1); fn.Elements["/C0"] = Arr(1, 0, 0); fn.Elements["/C1"] = Arr(0, 0, 1); fn.Elements["/N"] = new PdfInteger(1);
        var sh = new PdfDictionary(doc);
        sh.Elements["/ShadingType"] = new PdfInteger(2);
        sh.Elements["/ColorSpace"] = new PdfName("/DeviceRGB");
        sh.Elements["/Coords"] = Arr(400, 0, 560, 0);
        sh.Elements["/Function"] = fn;
        var ext = new PdfArray(doc); ext.Elements.Add(new PdfBoolean(true)); ext.Elements.Add(new PdfBoolean(true));
        sh.Elements["/Extend"] = ext;
        var shadings = new PdfDictionary(doc);
        shadings.Elements["/Sh1"] = sh;
        if (pattern)
        {
            var pat = new PdfDictionary(doc);
            pat.Elements["/PatternType"] = new PdfInteger(2);
            pat.Elements["/Shading"] = sh;
            var pats = new PdfDictionary(doc);
            pats.Elements["/P1"] = pat;
            page.Resources.Elements["/Pattern"] = pats;
            page.Contents.AppendContent().CreateStream(Encoding.Latin1.GetBytes("\n/Pattern cs /P1 scn 400 20 160 40 re f /Pattern CS /P1 SCN 8 w 400 100 150 50 re S\n"));
        }
        else
        {
            page.Resources.Elements["/Shading"] = shadings;
            page.Contents.AppendContent().CreateStream(Encoding.Latin1.GetBytes("\nq 400 20 160 40 re W n /Sh1 sh Q\n"));
        }
        doc.Save(dst);
        return dst;
    }

    /// <summary>PDFium 다시 쓰기가 못 쓰는 알려진 한계(M4x)인 셰이딩/패턴 + 5줄 문단(Helvetica 비임베드 → 앱 글꼴 줄)이 있는 400×400 쪽.</summary>
    private string ShadedParagraphPdf(string name, bool pattern)
    {
        string[] lines =
        [
            "The committee reviewed the annual budget and agreed that",
            "the proposed spending on new equipment should remain within",
            "the limits set last year while allowing a small increase for",
            "training and travel costs across the three regional offices and",
            "the central support team during the coming financial period.",
        ];
        var sb = new StringBuilder("BT /F1 10 Tf 20 380 Td 13 TL\n");
        for (int i = 0; i < lines.Length; i++) sb.Append(i == 0 ? "" : "T* ").Append($"({lines[i]}) Tj\n");
        sb.Append("ET\n");
        byte[] pdf = pattern
            ? ColorSpaceFidelityTests.Build("/Pattern << /P1 6 0 R >>", "/Pattern cs /P1 scn 20 150 360 120 re f /Pattern CS /P1 SCN 8 w 20 20 360 100 re S " + sb,
                [new("<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [20 0 380 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0.8 0] /C1 [0 0.5 1] /N 1 >> /Extend [true true] >> >>")])
            : ColorSpaceFidelityTests.Build("/Shading << /Sh1 << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [20 0 380 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> /Extend [true true] >> >>",
                "q 20 150 360 120 re W n /Sh1 sh Q " + sb, []);
        string path = Out(name);
        File.WriteAllBytes(path, pdf);
        return path;
    }

    // ---------------------------------------------------------------- 시험

    [Fact]
    public async Task ReplacedText_NotExtractableFromSavedFile()
    {
        var (s, page, m) = await OpenParagraph();
        var (edited, li) = ReverseWord(m);
        var plan = await Edit(s, page, m, edited);
        string saved = Out("replaced.pdf");
        await Svc(s).SaveAsync(s, saved);

        string text = Squash(await SavedText(saved));
        Assert.DoesNotContain(Squash(m.Lines[li].Text), text);          // 옛 줄 글이 추출로 나오지 않는다
        Assert.Contains(Squash(plan.Edit!.OriginalFontLines[0].Text), text);   // 새 줄은 나온다
        // 숨김(3 Tr)으로 남기지 않았다: 안전하게 지울 수 있는 글은 연산자째 사라진다
        string content = Encoding.Latin1.GetString(PageContentBytes(saved));
        Assert.DoesNotContain("3 Tr", content);
    }

    [Fact]
    public async Task OriginalFontLine_ReusesExistingFontDictionary()
    {
        var (s, page, m) = await OpenParagraph();
        var plan = await Edit(s, page, m, ReverseWord(m).Edited);
        Assert.False(plan.UsedAppFont);
        string saved = Out("reuse.pdf");
        await Svc(s).SaveAsync(s, saved);
        string baseline = Out("baseline.pdf");
        await Svc(s).SaveAsync(await DocumentSession.OpenAsync(_t, ParagraphTestData.Sample("doc-en-left").Path, null), baseline);

        Assert.Equal(FontObjectCount(baseline), FontObjectCount(saved));        // 새 글꼴 객체 없음
        Assert.Equal(PageFontNames(baseline), PageFontNames(saved));
        Assert.DoesNotContain(PageFontNames(saved), n => n.StartsWith("/SedolF"));
    }

    [Fact]
    public async Task AppFontLine_AddsSubsetFontOnce()
    {
        var (s, page, m) = await OpenParagraph();
        // 영문 글꼴에 없는 한글을 앞에 넣어 여러 줄이 다시 쓰이게 한다 → 다시 쓰는 줄 전체가 앱 글꼴
        var plan = await Edit(s, page, m, "한국어 문장 추가 " + ParagraphLayout.OriginalText(m));
        Assert.True(plan.UsedAppFont);
        Assert.True(plan.Edit!.AppLines.Length >= 2, "여러 줄이어야 시험이 된다");
        string saved = Out("appfont.pdf");
        await Svc(s).SaveAsync(s, saved);
        string baseline = Out("baseline2.pdf");
        await Svc(s).SaveAsync(await DocumentSession.OpenAsync(_t, ParagraphTestData.Sample("doc-en-left").Path, null), baseline);

        Assert.Equal(1, PageFontNames(saved).Count(n => n.StartsWith("/SedolF")));
        Assert.Equal(FontObjectCount(baseline) + 2, FontObjectCount(saved));   // Type0 + CIDFont 한 쌍
        Assert.Contains(Squash("한국어"), Squash(await SavedText(saved)));
    }

    [Fact]
    public async Task ParagraphPage_ContentRewritePath_UntouchedRegion_ZeroPixelDiff()
    {
        var (s, page, m) = await OpenParagraph();
        await Edit(s, page, m, ReverseWord(m).Edited);
        string saved = Out("zero.pdf");
        await Svc(s).SaveAsync(s, saved);

        // 직접 쓰기 경로: 원본 내용 바이트가 (q 한 줄 뒤에) 그대로 들어 있다 — PDFium이 쪽 내용을 다시 쓴 것이 아니다
        string orig = Encoding.Latin1.GetString(PageContentBytes(ParagraphTestData.Sample("doc-en-left").Path));
        string after = Encoding.Latin1.GetString(PageContentBytes(saved));
        Assert.Contains(orig[..Math.Min(80, orig.Length)], after);

        var box = m.Box;
        var a = await Render(ParagraphTestData.Sample("doc-en-left").Path);
        var b = await Render(saved);
        Assert.Equal(0, Diff(a, b, 595, 842, box, 8));
    }

    [Fact]
    public async Task ParagraphPage_ObjectMapRejected_FallsBackToPdfiumPath()
    {
        var (s, page, m) = await OpenParagraph();
        var plan = await Edit(s, page, m, ReverseWord(m).Edited);
        string saved = Out("fallback.pdf");
        await Svc(s, new RejectingProbe(new PdfSharpAnnotationPatcher(Font.Value), "object count differs")).SaveAsync(s, saved);

        // PDFium 경로: 쪽 내용을 PDFium이 다시 썼으므로 원본 내용 바이트가 그대로 있지 않다 — 그래도 새 글이 있고 옛 글이 없다
        string text = Squash(await SavedText(saved));
        Assert.Contains(Squash(plan.Edit!.OriginalFontLines[0].Text), text);
        Assert.DoesNotContain(Squash(m.Lines[plan.Edit.Remove.Length > 0 ? ReverseWord(m).Line : 0].Text), text);
        string orig = Encoding.Latin1.GetString(PageContentBytes(ParagraphTestData.Sample("doc-en-left").Path));
        Assert.DoesNotContain(orig[..Math.Min(80, orig.Length)], Encoding.Latin1.GetString(PageContentBytes(saved)));
    }

    [Fact]
    public async Task BothPathsRejected_SaveRefused_WithReason()
    {
        // 셰이딩이 있는 쪽 → PDFium 경로는 모양이 달라진다(M4x 알려진 한계); 직접 쓰기도 거절 → 저장 거절 + 이유
        string shaded = ShadedParagraphPdf("shaded-reject.pdf", false);
        var (s, page, m) = await OpenParagraph(path: shaded);
        await Edit(s, page, m, ReverseWord(m).Edited);
        string saved = Out("refused.pdf");
        var ex = await Assert.ThrowsAsync<SaveException>(() => Svc(s, new RejectingProbe(new PdfSharpAnnotationPatcher(Font.Value), "object count differs")).SaveAsync(s, saved));
        var inner = Assert.IsType<ContentRegenerationException>(ex.InnerException);
        Assert.Equal(1, inner.PageNumber);
        Assert.Contains("object count differs", inner.Reason);
        Assert.False(File.Exists(saved));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShadingPage_ParagraphEdit_Saves(bool pattern)
    {
        string shaded = ShadedParagraphPdf("shaded" + pattern + ".pdf", pattern);
        var (s, page, m) = await OpenParagraph(path: shaded);
        var plan = await Edit(s, page, m, ReverseWord(m).Edited);
        string saved = Out("shaded-out.pdf");
        await Svc(s).SaveAsync(s, saved);

        var box = m.Box;
        var a = await Render(shaded, 400, 400);
        var b = await Render(saved, 400, 400);
        Assert.Equal(0, Diff(a, b, 400, 400, box, 30));    // 셰이딩 포함 나머지 전부 0 px
        Assert.Contains(Squash(plan.Edit!.OriginalFontLines.Select(l => l.Text).Concat(plan.Edit.AppLines.Select(l => l.Text)).First()), Squash(await SavedText(saved)));
        Assert.Contains(pattern ? "scn" : "sh", Encoding.Latin1.GetString(PageContentBytes(saved)));
    }

    [Fact]
    public async Task SaveThreeTimes_SameContent()
    {
        var (s, page, m) = await OpenParagraph();
        await Edit(s, page, m, ReverseWord(m).Edited);
        var contents = new List<byte[]>();
        for (int i = 0; i < 3; i++)
        {
            string p = Out($"three{i}.pdf");
            await Svc(s).SaveAsync(s, p);
            contents.Add(PageContentBytes(p));
        }
        Assert.Equal(contents[0], contents[1]);
        Assert.Equal(contents[0], contents[2]);
    }

    [Fact]
    public async Task EditSaveUndoSave_OriginalContentBack()
    {
        var (s, page, m) = await OpenParagraph();
        string baseline = Out("undo-base.pdf");
        await Svc(s).SaveAsync(s, baseline);
        await Edit(s, page, m, ReverseWord(m).Edited);
        await Svc(s).SaveAsync(s, Out("undo-edited.pdf"));
        Assert.NotEqual(PageContentBytes(baseline), PageContentBytes(Out("undo-edited.pdf")));
        await s.Commands.UndoAsync();
        string back = Out("undo-back.pdf");
        await Svc(s).SaveAsync(s, back);
        Assert.Equal(PageContentBytes(baseline), PageContentBytes(back));
    }

    [Fact]
    public async Task AfterSave_LookEqualsScreen()
    {
        var (s, page, m) = await OpenParagraph();
        await Edit(s, page, m, ReverseWord(m).Edited);
        string saved = Out("look.pdf");
        await Svc(s).SaveAsync(s, saved);    // 저장 안의 충실도 보호(화면 모양 ≡ 저장 모양)를 통과해야 저장된다

        var screen = await _t.InvokeAsync(() =>
        {
            s.GetReplay(page);
            return s.GetLoadedPage(page).RenderTile(1.0, 0, 0, 595, 842, false);
        });
        var file = await Render(saved);
        Assert.Equal(0, Diff(screen, file, 595, 842, null));
    }

    [Fact]
    public async Task JustifiedParagraph_Saves_GuardMasksUnrenderableLines()
    {
        // 낱말마다 글 객체인 양쪽 맞춤 문서: 화면의 PDFium 글 객체는 늘임을 못 그린다 → 보호가 그 줄 주변을 비교에서 빼고 나머지는 엄격
        var (s, page, m) = await OpenParagraph("doc-en-justified");
        var (edited, _) = ReverseWord(m);
        var plan = await _t.InvokeAsync(() => ParagraphEditPlanner.Plan(s, page, m, edited, Font.Value));
        Assert.True(plan.Ok && plan.Edit is not null, "plan " + plan.Refusal);
        await s.Commands.ExecuteAsync(plan.ToCommand(page)!);
        string saved = Out("justified.pdf");
        await Svc(s).SaveAsync(s, saved);
        Assert.Contains(Squash(plan.Edit.AllNewText()), Squash(await SavedText(saved)));
    }

    [Fact]
    public async Task NoParagraphEdits_NoCostAndBytesIdentical()
    {
        var s = await DocumentSession.OpenAsync(_t, ParagraphTestData.Sample("doc-en-left").Path, null);
        string a = Out("noedit-a.pdf"), b = Out("noedit-b.pdf");
        await Svc(s).SaveAsync(s, a);
        await Svc(s).SaveAsync(s, b);
        Assert.False(s.Content.HasEdits);
        Assert.Equal(PageContentBytes(a), PageContentBytes(b));
        var builder = new OutputCopyBuilder(null, new PdfSharpAnnotationPatcher(Font.Value), s.Assets);
        Assert.Null(await builder.BuildIfNeededAsync(s, Out("noedit-c.pdf")));    // 인쇄 경로: 사본을 만들지 않는다
        Assert.False(File.Exists(Out("noedit-c.pdf")));
    }
}

internal static class ParagraphEditTestExtensions
{
    public static string AllNewText(this ReplaceParagraphEdit e) => string.Concat(e.OriginalFontLines.Select(l => l.Text));
}


