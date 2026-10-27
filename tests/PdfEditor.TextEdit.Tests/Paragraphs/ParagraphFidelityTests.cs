using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.Document.Content;
using PdfEditor.Document.Output;
using PdfEditor.Document.Recovery;
using PdfEditor.Document.Saving;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;
using PdfEditor.TextEdit.Paragraphs;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit.Abstractions;

namespace PdfEditor.TextEdit.Tests.Paragraphs;

/// <summary>
/// M5 Task 9: 문단 편집의 충실도·왕복·호환성 시험. 모두 실제 저장 길(OutputCopyBuilder → PDFsharp 패치)로 저장한 뒤 다시 열어 확인한다.
/// 다른 뷰어(Acrobat·PDF-XChange·Foxit·Chrome)에서의 모양은 자동으로 못 잡는다(사용자 점검).
/// </summary>
public sealed class ParagraphFidelityTests(ITestOutputHelper log) : IDisposable
{
    private static readonly Lazy<AppFont> Font = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        return AppFont.Load(Path.Combine(dir!.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"));
    });

    private static readonly Lazy<AppFontCatalog> Catalog = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        return AppFontCatalog.ForBaseDirectory(Font.Value, dir!.FullName);
    });

    private readonly PdfiumThread _t = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-m5task9-" + Guid.NewGuid().ToString("N"));

    private readonly List<DocumentSession> _sessions = [];

    public void Dispose()
    {
        foreach (var s in _sessions) s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Out(string name)
    {
        Directory.CreateDirectory(_dir);
        return Path.Combine(_dir, name);
    }

    private static string SpikeOut([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "spikes", "out"));

    // ---------------------------------------------------------------- 도우미

    private async Task<DocumentSession> Open(string path)
    {
        var s = await DocumentSession.OpenAsync(_t, path, null);
        _sessions.Add(s);
        return s;
    }

    /// <summary>쪽에서 문단으로 읽히는 모델들(줄 수 minLines 이상, 첫 줄 기준 중복 없음). 위→아래 순서.</summary>
    private async Task<List<ParagraphModel>> Paragraphs(DocumentSession s, PageId page, int minLines)
    {
        return await _t.InvokeAsync(() =>
        {
            var idx = s.GetObjectIndex(page);
            var seen = new HashSet<ObjectRef>();
            var list = new List<ParagraphModel>();
            foreach (var e in idx.Entries.Where(e => e.Type == PdfPageObjectType.Text && !e.Ref.IsInserted).ToList())
            {
                if (ParagraphReader.Detect(s, page, e.Ref, idx).Model is not { IsParagraph: true } m || m.Lines.Length < minLines) continue;
                if (seen.Overlaps(m.AllRefs)) continue;
                foreach (var r in m.AllRefs) seen.Add(r);
                list.Add(m);
            }
            return list.OrderByDescending(m => m.Box.Top).ToList();
        });
    }

    /// <summary>가운데 줄(없으면 첫 줄)의 낱말 하나를 글자 순서만 뒤집은 글(같은 길이, 같은 글자 → 서브셋 글꼴에도 있음).</summary>
    private static string ReverseWord(ParagraphModel m)
    {
        string orig = ParagraphLayout.OriginalText(m);
        for (int li = Math.Min(1, m.Lines.Length - 1); li < m.Lines.Length; li++)
            foreach (var w in m.Lines[li].Text.Split(' '))
            {
                if (w.Length < 3 || w.Distinct().Count() <= 2 || !w.All(char.IsLetter)) continue;
                int lineStart = orig.IndexOf(m.Lines[li].Text, StringComparison.Ordinal);
                if (lineStart < 0) continue;
                int at = lineStart + m.Lines[li].Text.IndexOf(w, StringComparison.Ordinal);
                string rev = new(w.Reverse().ToArray());
                if (rev == w) continue;
                return orig[..at] + rev + orig[(at + w.Length)..];
            }
        throw new InvalidOperationException("no reversible word");
    }

    /// <summary>편집이 건드릴 수 있는 쪽 영역: 원래 문단 상자 + 다시 배치한 줄들이 차지하는 사각형(줄 수가 늘면 상자 아래로 자란다).</summary>
    private static PdfRectF Region(ParagraphModel m, ParagraphPlan plan)
    {
        var r = m.Box;
        if (plan.Layout is not { } l) return r;
        int angle = m.Lines[0].Angle;
        var (ux, uy, vx, vy) = Geo.Axes(angle);
        float size = m.Style.SizePt;
        foreach (var line in l.Lines)
        {
            PdfPointF P(double u, double v) => new((float)(line.BaselineOrigin.X + u * ux + v * vx), (float)(line.BaselineOrigin.Y + u * uy + v * vy));
            var lr = PdfRectF.FromPoints([P(0, -0.3 * size), P(line.Width, -0.3 * size), P(0, 0.9 * size), P(line.Width, 0.9 * size)]);
            r = new PdfRectF(Math.Min(r.Left, lr.Left), Math.Min(r.Bottom, lr.Bottom), Math.Max(r.Right, lr.Right), Math.Max(r.Top, lr.Top));
        }
        return r;
    }

    private async Task<ParagraphPlan> Edit(DocumentSession s, PageId page, ParagraphModel m, string text, ReplaceParagraphEdit? previous = null)
    {
        var plan = await _t.InvokeAsync(() => ParagraphEditPlanner.Plan(s, page, m, text, Font.Value, previous, KoreanLineBreak.Word, null, Catalog.Value));
        Assert.True(plan.Ok, plan.Refusal.ToString());
        Assert.NotNull(plan.Edit);
        await s.Commands.ExecuteAsync(plan.ToCommand(page)!);
        return plan;
    }

    private SaveService Svc(DocumentSession s) => new([], null, new PdfSharpAnnotationPatcher(Font.Value) { SerifFont = () => Catalog.Value.Serif }, s.Assets);

    private OutputCopyBuilder Builder(DocumentSession s) => new(null, new PdfSharpAnnotationPatcher(Font.Value) { SerifFont = () => Catalog.Value.Serif }, s.Assets);

    private Task<string> TextOf(string pdf, int pageIndex = 0) => _t.InvokeAsync(() =>
    {
        using var d = Pdf.Native.PdfDocument.Open(_t, pdf, null);
        using var p = d.LoadPage(pageIndex);
        using var tp = p.LoadText();
        return tp.GetText(0, tp.CharCount);
    });

    private static string Squash(string t) => string.Concat(t.Where(c => !char.IsWhiteSpace(c)));

    private Task<(byte[] Bgra, int W, int H)> Render(string pdf, int pageIndex = 0) => _t.InvokeAsync(() =>
    {
        using var d = Pdf.Native.PdfDocument.Open(_t, pdf, null);
        using var p = d.LoadPage(pageIndex);
        var size = p.SizePt;
        int w = (int)Math.Ceiling(size.Width), h = (int)Math.Ceiling(size.Height);
        return (p.RenderTile(1.0, 0, 0, w, h, false), w, h);
    });

    /// <summary>두 그림이 다른 픽셀 수(채널 8 초과). skip(쪽 공간, 아래가 원점)과 그 둘레 margin 안은 세지 않는다. 쪽 회전 0만.</summary>
    private static int Diff(byte[] a, byte[] b, int w, int h, IEnumerable<PdfRectF> skips, float margin = 8)
    {
        var rects = skips.ToList();
        int diff = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool skip = false;
                foreach (var r in rects)
                    if (x >= r.Left - margin && x <= r.Right + margin && h - y >= r.Bottom - margin && h - y <= r.Top + margin) { skip = true; break; }
                if (skip) continue;
                int i = (y * w + x) * 4;
                if (Math.Abs(a[i] - b[i]) > 8 || Math.Abs(a[i + 1] - b[i + 1]) > 8 || Math.Abs(a[i + 2] - b[i + 2]) > 8) diff++;
            }
        return diff;
    }

    /// <summary>색 차이가 큰(채널 100 초과) 픽셀 수 — 글꼴 글자와 윤곽선 경로 글자의 안티앨리어싱 차이는 세지 않는다.</summary>
    private static int StrongDiff(byte[] a, byte[] b, int w, int h)
    {
        int d = 0;
        for (int i = 0; i < w * h * 4; i += 4)
            if (Math.Abs(a[i] - b[i]) > 100 || Math.Abs(a[i + 1] - b[i + 1]) > 100 || Math.Abs(a[i + 2] - b[i + 2]) > 100) d++;
        return d;
    }

    /// <summary>두 그림이 다른 픽셀들을 감싸는 사각형(쪽 좌표, 아래가 원점)과 개수. region 안은 세지 않는다. 진단용.</summary>
    private async Task<string> DiffWhere(string before, string after, PdfRectF region, int pageIndex)
    {
        var (a, w, h) = await Render(before, pageIndex);
        var (b, _, _) = await Render(after, pageIndex);
        int n = 0; double l = 1e9, r = -1e9, bt = 1e9, tp = -1e9;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (x >= region.Left - 8 && x <= region.Right + 8 && h - y >= region.Bottom - 8 && h - y <= region.Top + 8) continue;
                int i = (y * w + x) * 4;
                if (Math.Abs(a[i] - b[i]) > 8 || Math.Abs(a[i + 1] - b[i + 1]) > 8 || Math.Abs(a[i + 2] - b[i + 2]) > 8)
                { n++; l = Math.Min(l, x); r = Math.Max(r, x); bt = Math.Min(bt, h - y); tp = Math.Max(tp, h - y); }
            }
        return $"{n}px 위치 x {l:0}~{r:0} y {bt:0}~{tp:0} (쪽 {w}x{h}), 편집 영역 {region.Left:0},{region.Bottom:0}~{region.Right:0},{region.Top:0}";
    }

    private async Task<int> OutsideDiff(string before, string after, IEnumerable<PdfRectF> boxes, int pageIndex = 0, float margin = 8)
    {
        var (a, w, h) = await Render(before, pageIndex);
        var (b, _, _) = await Render(after, pageIndex);
        return Diff(a, b, w, h, boxes, margin);
    }

    private static string AllContent(string pdf, int pageIndex = 0)
    {
        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        return Encoding.Latin1.GetString(PdfEditor.ContentStream.PageContent.Load(doc.Pages[pageIndex]).Data ?? []);
    }

    private static List<string> FontNames(string pdf)
    {
        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        return doc.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Where(d => d.Elements.GetName("/Type") == "/Font" && d.Elements.GetName("/BaseFont") is { Length: > 0 })
            .Select(d => d.Elements.GetName("/BaseFont")).ToList();
    }

    private string ThreePages(string src, string name)
    {
        string dst = Out(name);
        using var s = PdfReader.Open(src, PdfDocumentOpenMode.Import);
        using var d = new PdfSharp.Pdf.PdfDocument();
        for (int i = 0; i < 3; i++) d.AddPage(s.Pages[0]);
        d.Save(dst);
        return dst;
    }

    // 셰이딩(sh)/패턴 쪽: M4x 알려진 한계 — PDFium 다시 쓰기가 못 쓰는 쪽(5줄 문단, 400×400)
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

    // ---------------------------------------------------------------- 편집 밖 영역 0 px

    public static TheoryData<string> OutsideSamples => new()
    {
        "doc-en-left", "doc-en-justified", "doc-ko-left-wordwrap", "doc-heading-bullets", "doc-center-right",
        "dwg-notes", "dwg-rot90",
    };

    /// <summary>문서형·도면형: 쪽의 문단을 하나씩 같은 길이로 고쳐 저장하면 상자 둘레 밖은 한 픽셀도 달라지지 않는다.</summary>
    [Theory]
    [MemberData(nameof(OutsideSamples))]
    public async Task ParagraphEdit_OutsideRegion_ZeroPixelDiff(string sample)
    {
        string src = ParagraphTestData.Sample(sample).Path;
        var s = await Open(src);
        var page = s.Pages[0].Id;
        var paras = await Paragraphs(s, page, 2);
        Assert.NotEmpty(paras);
        int tried = 0;
        var boxes = new List<PdfRectF>();
        foreach (var m in paras.Take(2))
        {
            string text;
            try { text = ReverseWord(m); } catch (InvalidOperationException) { continue; }
            var plan = await _t.InvokeAsync(() => ParagraphEditPlanner.Plan(s, page, m, text, Font.Value, null, KoreanLineBreak.Word, null, Catalog.Value));
            if (!plan.Ok || plan.Edit is null) continue;
            await s.Commands.ExecuteAsync(plan.ToCommand(page)!);
            boxes.Add(Region(m, plan));
            tried++;
        }
        Assert.True(tried > 0, "고칠 수 있는 문단이 없다");
        string saved = Out(sample + "-edited.pdf");
        await Svc(s).SaveAsync(s, saved);
        int diff = await OutsideDiff(src, saved, boxes);
        log.WriteLine($"{sample}: 문단 {tried}개 편집, 상자 밖 다른 픽셀 {diff}");
        Assert.Equal(0, diff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParagraphEdit_ShadingAndPatternPages_ZeroPixelDiff(bool pattern)
    {
        string src = ShadedParagraphPdf("shade" + pattern + ".pdf", pattern);
        var s = await Open(src);
        var page = s.Pages[0].Id;
        var m = (await Paragraphs(s, page, 3)).First();
        await Edit(s, page, m, ReverseWord(m));
        string saved = Out("shade-out" + pattern + ".pdf");
        await Svc(s).SaveAsync(s, saved);
        Assert.Equal(0, await OutsideDiff(src, saved, [m.Box], margin: 30));
    }

    /// <summary>글 크기를 Tf가 아니라 글 행렬(Tm)의 배율로 주는 PDF(실제 PDF에서 흔함: "/F0 1 Tf 11 0 0 11 x y Tm"). doc-en-justified의 내용을 같은 모양이 되도록 다시 쓴다.</summary>
    private string ScaledMatrixJustified(string name)
    {
        string src = ParagraphTestData.Sample("doc-en-justified").Path;
        string dst = Out(name);
        using var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify);
        var page = doc.Pages[0];
        string c = Encoding.Latin1.GetString(PdfEditor.ContentStream.PageContent.Load(page).Data!);
        Assert.Contains("/F0 11 Tf", c);
        c = c.Replace("/F0 11 Tf", "/F0 1 Tf 11 0 0 11 0 0 Tm");
        c = Regex.Replace(c, @"(-?[\d.]+) (-?[\d.]+) Td", m => $"{(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) / 11).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)} {(double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) / 11).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)} Td");
        page.Contents.Elements.Clear();
        page.Contents.AppendContent().CreateStream(Encoding.Latin1.GetBytes(c));
        doc.Save(dst);
        return dst;
    }

    /// <summary>실제 PDF(2026-10-27 발견)의 결함 재현: 글 크기가 Tm 배율로 주어진 양쪽 맞춤 줄을 고치면 낱말 사이가 배율만큼(여기서 11배) 벌어져 줄이 상자 밖으로 넘쳤다.</summary>
    [Fact]
    public async Task ParagraphEdit_ScaledTextMatrix_JustifiedLine_StaysInsideRegion()
    {
        string plain = ParagraphTestData.Sample("doc-en-justified").Path;
        string src = ScaledMatrixJustified("scaled-justified.pdf");
        var (ra, rw, rh) = await Render(plain);
        var (rb, _, _) = await Render(src);
        Assert.Equal(0, StrongDiff(ra, rb, rw, rh));    // 다시 쓴 원본이 같은 모양이다(소수점 반올림 안티앨리어싱은 무시)
        var s = await Open(src);
        var page = s.Pages[0].Id;
        var m = (await Paragraphs(s, page, 3)).First();
        var plan = await Edit(s, page, m, ReverseWord(m));
        Assert.Contains(plan.Layout!.Lines, l => !l.IsOriginal && Math.Abs(l.JustifyExtra) > 0.01f);   // 늘임이 있는 줄을 다시 쓴다
        string saved = Out("scaled-justified-out.pdf");
        await Svc(s).SaveAsync(s, saved);
        Assert.Equal(0, await OutsideDiff(src, saved, [Region(m, plan)]));
    }

    // ---------------------------------------------------------------- 저장 → 다시 열기 → 다시 편집

    [Fact]
    public async Task EditSaveReopenEdit_RoundTrip()
    {
        string src = ParagraphTestData.Sample("doc-en-left").Path;
        var s = await Open(src);
        var page = s.Pages[0].Id;
        var m = (await Paragraphs(s, page, 3)).First();
        string first = ReverseWord(m);
        await Edit(s, page, m, first);
        string saved1 = Out("rt1.pdf");
        await Svc(s).SaveAsync(s, saved1);

        // 저장한 파일을 새로 열어 같은 문단을 다시 추정한다(고친 줄이 원래 줄처럼 읽혀야 한다)
        var s2 = await Open(saved1);
        var page2 = s2.Pages[0].Id;
        var paras2 = await Paragraphs(s2, page2, 3);
        var m2 = paras2.First(p => Math.Abs(p.Box.Top - m.Box.Top) < 3);
        Assert.Equal(m.Lines.Length, m2.Lines.Length);
        Assert.Equal(Squash(first), Squash(ParagraphLayout.OriginalText(m2)));   // 고친 글이 문단 글로 읽힌다
        string second = ParagraphLayout.OriginalText(m2).Replace(m2.Lines[0].Text.Split(' ')[0], "Revised");
        var planB = await Edit(s2, page2, m2, second);
        string saved2 = Out("rt2.pdf");
        await Svc(s2).SaveAsync(s2, saved2);
        string text = Squash(await TextOf(saved2));
        Assert.Contains("Revised", text);
        Assert.Contains(Squash(second)[..60], text);
        // 옛 줄(첫 번째 고침 글, 첫 단어 제외)이 겹쳐 남지 않는다
        Assert.Equal(1, CountOf(text, Squash(m2.Lines[1].Text)));
        Assert.DoesNotContain("3 Tr", AllContent(saved2));
        Assert.Equal(0, await OutsideDiff(saved1, saved2, [Region(m2, planB)]));
    }

    private static int CountOf(string hay, string needle)
    {
        int n = 0, i = 0;
        while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    [Fact]
    public async Task UndoParagraph_RestoresOriginalLines()
    {
        string src = ParagraphTestData.Sample("doc-en-left").Path;
        var s = await Open(src);
        var page = s.Pages[0].Id;
        var m = (await Paragraphs(s, page, 3)).First();
        string baseline = Out("u0.pdf");
        await Svc(s).SaveAsync(s, baseline);
        await Edit(s, page, m, ReverseWord(m));
        string edited = Out("u1.pdf");
        await Svc(s).SaveAsync(s, edited);
        Assert.NotEqual(Squash(await TextOf(baseline)), Squash(await TextOf(edited)));
        await s.Commands.UndoAsync();
        string undone = Out("u2.pdf");
        await Svc(s).SaveAsync(s, undone);
        Assert.Equal(Squash(await TextOf(baseline)), Squash(await TextOf(undone)));
        Assert.Equal(0, await OutsideDiff(baseline, undone, []));          // 전체 0 px
        await s.Commands.RedoAsync();
        string redone = Out("u3.pdf");
        await Svc(s).SaveAsync(s, redone);
        Assert.Equal(Squash(await TextOf(edited)), Squash(await TextOf(redone)));
        Assert.Equal(0, await OutsideDiff(edited, redone, []));
    }

    // ---------------------------------------------------------------- 쪽 작업과 섞임

    [Fact]
    public async Task ParagraphThenPageOps_EditFollowsPage()
    {
        string src = ThreePages(ParagraphTestData.Sample("doc-en-left").Path, "three.pdf");
        var s = await Open(src);
        var edited = s.Pages[1].Id;
        var other = s.Pages[2].Id;
        var m = (await Paragraphs(s, edited, 3)).First();
        string newText = ReverseWord(m);
        var plan = await Edit(s, edited, m, newText);
        string marker = Squash(plan.Edit!.OriginalFontLines.Select(l => l.Text).Concat(plan.Edit.AppLines.Select(l => l.Text)).First());

        async Task<int> PageWithEdit(string path)
        {
            int n = await _t.InvokeAsync(() => { using var d = Pdf.Native.PdfDocument.Open(_t, path, null); return d.PageCount; });
            for (int i = 0; i < n; i++) if (Squash(await TextOf(path, i)).Contains(marker)) return i;
            return -1;
        }

        string p0 = Out("ops0.pdf");
        await Svc(s).SaveAsync(s, p0);
        Assert.Equal(1, await PageWithEdit(p0));

        await s.Commands.ExecuteAsync(new RotatePagesCommand([edited], 1));
        string p1 = Out("ops1.pdf");
        await Svc(s).SaveAsync(s, p1);
        Assert.Equal(1, await PageWithEdit(p1));
        using (var doc = PdfReader.Open(p1, PdfDocumentOpenMode.Import)) Assert.Equal(90, doc.Pages[1].Rotate);

        await s.Commands.ExecuteAsync(new MovePagesCommand([edited], 0));
        string p2 = Out("ops2.pdf");
        await Svc(s).SaveAsync(s, p2);
        Assert.Equal(0, await PageWithEdit(p2));

        await s.Commands.ExecuteAsync(new DeletePagesCommand([other]));
        string p3 = Out("ops3.pdf");
        await Svc(s).SaveAsync(s, p3);
        Assert.Equal(0, await PageWithEdit(p3));
        using (var doc = PdfReader.Open(p3, PdfDocumentOpenMode.Import)) Assert.Equal(2, doc.PageCount);

        for (int i = 0; i < 3; i++) await s.Commands.UndoAsync();   // 삭제·이동·회전 취소
        Assert.Single(s.Content.For(edited).OfType<ReplaceParagraphEdit>());
        string p4 = Out("ops4.pdf");
        await Svc(s).SaveAsync(s, p4);
        Assert.Equal(1, await PageWithEdit(p4));
        Assert.Equal(0, await OutsideDiff(p0, p4, [], pageIndex: 1));

        await s.Commands.UndoAsync();   // 문단 편집 취소
        Assert.Empty(s.Content.For(edited));
        string p5 = Out("ops5.pdf");
        await Svc(s).SaveAsync(s, p5);
        Assert.Equal(-1, await PageWithEdit(p5));

        // 편집한 쪽을 지우고 실행 취소 → 편집이 돌아온다
        await s.Commands.RedoAsync();
        await s.Commands.ExecuteAsync(new DeletePagesCommand([edited]));
        await s.Commands.UndoAsync();
        Assert.Single(s.Content.For(edited).OfType<ReplaceParagraphEdit>());
    }

    // ---------------------------------------------------------------- 인쇄·복구·요약·쪽 추출 경로 (모두 OutputCopyBuilder)

    private async Task<(DocumentSession S, PageId Page, ParagraphModel M, string Marker)> EditedDoc(string sample = "doc-en-left")
    {
        string src = ParagraphTestData.Sample(sample).Path;
        var s = await Open(src);
        var page = s.Pages[0].Id;
        var m = (await Paragraphs(s, page, 3)).First();
        var plan = await Edit(s, page, m, ReverseWord(m));
        string marker = Squash(plan.Edit!.OriginalFontLines.Select(l => l.Text).Concat(plan.Edit.AppLines.Select(l => l.Text)).First());
        return (s, page, m, marker);
    }

    [Fact]
    public async Task Print_ShowsEditedParagraph()
    {
        var (s, _, m, marker) = await EditedDoc();
        string copy = Out("print.pdf");
        var built = await Builder(s).BuildIfNeededAsync(s, copy);   // 인쇄가 쓰는 길
        Assert.NotNull(built);
        Assert.Contains(marker, Squash(await TextOf(copy)));
        string saved = Out("print-saved.pdf");
        await Svc(s).SaveAsync(s, saved);
        Assert.Equal(0, await OutsideDiff(copy, saved, []));   // 인쇄본 ≡ 저장본(전체 0 px)
    }

    [Fact]
    public async Task Recovery_ContainsParagraphEdit()
    {
        var (s, _, _, marker) = await EditedDoc();
        string folder = Out("recovery");
        var store = new RecoveryStore(folder, null, new PdfSharpAnnotationPatcher(Font.Value) { SerifFont = () => Catalog.Value.Serif }, s.Assets);
        await store.WriteAsync(s);
        var entry = Assert.Single(store.List());
        Assert.Contains(marker, Squash(await TextOf(entry.RecoveryPdfPath)));
        Assert.True(s.IsDirty);   // 복구본을 써도 세션은 더티 그대로
    }

    [Fact]
    public async Task Summary_PageImageShowsEdits()
    {
        // 주석 요약의 쪽 그림은 인쇄와 같은 사본(BuildIfNeededAsync)에서 그린다 — 편집이 그림에 보인다
        var (s, _, m, _) = await EditedDoc();
        string orig = ParagraphTestData.Sample("doc-en-left").Path;
        string copy = Out("summary.pdf");
        Assert.NotNull(await Builder(s).BuildIfNeededAsync(s, copy));
        var (a, w, h) = await Render(orig);
        var (b, _, _) = await Render(copy);
        int inside = Diff(a, b, w, h, [new PdfRectF(0, 0, w, h)], 0);   // 전부 건너뛰면 0 — 아래에서 상자 안만 센다
        Assert.Equal(0, inside);
        int total = Diff(a, b, w, h, [], 0);
        Assert.True(total > 0, "상자 안에 변화가 보여야 한다");
        Assert.Equal(0, Diff(a, b, w, h, [m.Box], 8));
    }

    [Fact]
    public async Task ExtractPages_IncludesParagraphEdit()
    {
        string src = ThreePages(ParagraphTestData.Sample("doc-en-left").Path, "ext-src.pdf");
        var s = await Open(src);
        var edited = s.Pages[1].Id;
        var m = (await Paragraphs(s, edited, 3)).First();
        var plan = await Edit(s, edited, m, ReverseWord(m));
        string marker = Squash(plan.Edit!.OriginalFontLines.Select(l => l.Text).Concat(plan.Edit.AppLines.Select(l => l.Text)).First());
        string target = Out("extracted.pdf");
        await PageExtractor.ExtractAsync(s, Builder(s), [1], target);
        using (var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import)) Assert.Equal(1, doc.PageCount);
        Assert.Contains(marker, Squash(await TextOf(target)));
    }

    // ---------------------------------------------------------------- 90° 도면 주기

    [Fact]
    public async Task Rotated90Drawing_NoteParagraph_Edit()
    {
        string src = ParagraphTestData.Sample("dwg-rot90").Path;
        var s = await Open(src);
        var page = s.Pages[0].Id;
        var paras = await Paragraphs(s, page, 3);
        var rotated = paras.FirstOrDefault(p => p.Lines[0].Angle == 90);
        Assert.NotNull(rotated);
        var plan = await Edit(s, page, rotated!, ReverseWord(rotated!));
        string saved = Out("rot90.pdf");
        await Svc(s).SaveAsync(s, saved);
        string marker = Squash(plan.Edit!.OriginalFontLines.Select(l => l.Text).Concat(plan.Edit.AppLines.Select(l => l.Text)).First());
        Assert.Contains(marker, Squash(await TextOf(saved)));
        Assert.Equal(0, await OutsideDiff(src, saved, [Region(rotated!, plan)]));
        // 가로 문단은 그대로 읽힌다
        string orig = Squash(await TextOf(src));
        string now = Squash(await TextOf(saved));
        var horizontal = paras.First(p => p.Lines[0].Angle == 0);
        Assert.Contains(Squash(horizontal.Lines[0].Text), now);
        Assert.NotEqual(orig, now);
    }

    // ---------------------------------------------------------------- 한글 / 서브셋에 없는 글자

    [Fact]
    public async Task KoreanParagraph_FontsEmbeddedOnce_NoTofu()
    {
        string src = ParagraphTestData.Sample("doc-en-left").Path;
        var s = await Open(src);
        var page = s.Pages[0].Id;
        var paras = await Paragraphs(s, page, 3);
        string[] added = ["한글 문장을 앞에 붙입니다", "서브셋에 없는 글자들"];
        for (int i = 0; i < 2; i++) await Edit(s, page, paras[i], added[i] + " " + ParagraphLayout.OriginalText(paras[i]));
        string saved = Out("korean.pdf");
        await Svc(s).SaveAsync(s, saved);
        string text = Squash(await TextOf(saved));
        foreach (var a in added) Assert.Contains(Squash(a), text);   // 추출 글 일치(네모가 아니다)
        var fonts = FontNames(saved);
        log.WriteLine("글꼴: " + string.Join(", ", fonts));
        Assert.Equal(2, fonts.Count(f => f.Contains("NotoSansKR")));   // Type0 + CIDFont 한 쌍(= 서브셋 한 번)
        // 저장 모양 ≡ 화면 모양(화면은 글꼴 윤곽선을 직접 그리므로 글리프가 없으면 두 그림이 달라진다)
        var screen = await _t.InvokeAsync(() =>
        {
            s.GetReplay(page);
            return s.GetLoadedPage(page).RenderTile(1.0, 0, 0, 595, 842, false);
        });
        var (file, _, _) = await Render(saved);
        int strong = StrongDiff(screen, file, 595, 842);
        log.WriteLine($"한글 문단: 화면(윤곽선) vs 저장(글꼴) 강한 차이 픽셀 {strong} / 흐린 차이 포함 {Diff(screen, file, 595, 842, [], 0)}");
        Assert.True(strong < 600, $"강한 차이 {strong}px");
    }

    // ---------------------------------------------------------------- 실제 PDF (private/ 가 있을 때만, 수치만)

    [Fact]
    public async Task RealPdfs_PrivateOptional()
    {
        string? priv = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "private"))) { priv = Path.Combine(d.FullName, "private"); break; }
        if (priv is null) { log.WriteLine("private/ 없음 — 건너뜀"); return; }

        int maxPages = int.TryParse(Environment.GetEnvironmentVariable("M5_REAL_MAXPAGES"), out var mp) ? mp : 12;
        int pagesTried = 0, withParagraph = 0, singleLineOnly = 0, planned = 0, planFailed = 0, saved = 0, saveRefused = 0, appFont = 0, diffBad = 0;
        long diffPixels = 0;
        var report = new List<string>();
        var rnd = new Random(5);
        foreach (var file in Directory.GetFiles(priv, "*.pdf", SearchOption.TopDirectoryOnly).OrderBy(f => f))
        {
            if (pagesTried >= maxPages) break;
            DocumentSession s;
            try { s = await Open(file); } catch (Exception ex) { report.Add($"{Path.GetFileName(file)}: 열기 실패 {ex.GetType().Name}"); continue; }
            int n = s.Pages.Length;
            var pick = Enumerable.Range(0, n).OrderBy(_ => rnd.Next()).Take(Math.Min(n, 4)).OrderBy(i => i).ToList();
            foreach (int pi in pick)
            {
                if (pagesTried >= maxPages) break;
                pagesTried++;
                var page = s.Pages[pi].Id;
                try
                {
                    var (paras, textCount) = await _t.InvokeAsync(() =>
                    {
                        var idx = s.GetObjectIndex(page);
                        var texts = idx.Entries.Where(e => e.Type == PdfPageObjectType.Text && !e.Ref.IsInserted).Take(60).ToList();
                        var found = new List<ParagraphModel>();
                        int single = 0;
                        var seen = new HashSet<ObjectRef>();
                        foreach (var e in texts)
                        {
                            var det = ParagraphReader.Detect(s, page, e.Ref, idx);
                            if (det.Model is { IsParagraph: true, Lines.Length: >= 3 } m && !seen.Overlaps(m.AllRefs)) { found.Add(m); foreach (var r in m.AllRefs) seen.Add(r); }
                            else single++;
                        }
                        return (found, single);
                    });
                    singleLineOnly += textCount > 0 && paras.Count == 0 ? 1 : 0;
                    if (paras.Count == 0) continue;
                    withParagraph++;
                    var m0 = paras[0];
                    string text;
                    try { text = ReverseWord(m0); } catch (InvalidOperationException) { continue; }
                    var plan = await _t.InvokeAsync(() => ParagraphEditPlanner.Plan(s, page, m0, text, Font.Value, null, KoreanLineBreak.Word, null, Catalog.Value));
                    if (!plan.Ok || plan.Edit is null) { planFailed++; report.Add($"{Path.GetFileName(file)} p{pi + 1}: 계획 거절 {plan.Refusal}"); continue; }
                    planned++;
                    if (plan.UsedAppFont) appFont++;
                    await s.Commands.ExecuteAsync(plan.ToCommand(page)!);
                    string outPath = Out($"real{pagesTried}.pdf");
                    try { await Svc(s).SaveAsync(s, outPath); }
                    catch (SaveException ex) { saveRefused++; report.Add($"{Path.GetFileName(file)} p{pi + 1}: 저장 거절 {ex.InnerException?.GetType().Name}"); await s.Commands.UndoAsync(); continue; }
                    saved++;
                    // 편집한 쪽만 비교(상자 밖 0 px가 목표)
                    string orig = Out($"real{pagesTried}-orig.pdf");
                    var sOrig = await Open(file);
                    await Svc(sOrig).SaveAsync(sOrig, orig);
                    int d = await OutsideDiff(orig, outPath, [Region(m0, plan)], pi, 8);
                    diffPixels += d;
                    if (d > 0) { diffBad++; report.Add($"{Path.GetFileName(file)} p{pi + 1}: 상자 밖 다른 픽셀 " + await DiffWhere(orig, outPath, Region(m0, plan), pi)); }
                    await s.Commands.UndoAsync();
                }
                catch (Exception ex) { report.Add($"{Path.GetFileName(file)} p{pi + 1}: 예외 {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        string summary = $"쪽 {pagesTried}개: 문단(3줄 이상) 있음 {withParagraph}, 글만 있고 문단 없음(한 줄 편집으로) {singleLineOnly}, 계획 성공 {planned} (앱 글꼴 {appFont}), 계획 거절 {planFailed}, 저장 성공 {saved}, 저장 거절 {saveRefused}, 상자 밖 달라진 쪽 {diffBad} (픽셀 합 {diffPixels})";
        log.WriteLine(summary);
        foreach (var r in report) log.WriteLine("  " + r);
        Directory.CreateDirectory(SpikeOut());
        File.WriteAllText(Path.Combine(SpikeOut(), "m5-real-pdf-stats.txt"), summary + "\n" + string.Join("\n", report.Select(r => Regex.Replace(r, @"^.*?(?=p\d+:)", "<파일> ")))); // 수치만(파일 이름 제외)
        Assert.Equal(0, saveRefused + diffBad);
    }

    // ---------------------------------------------------------------- 다른 뷰어용 호환성 샘플 (docs/checklists/viewer-compat.md M5 절)

    private void WritePng(string pdf, string png)
    {
        var (bgra, w, h) = _t.InvokeAsync(() =>
        {
            using var d = Pdf.Native.PdfDocument.Open(_t, pdf, null);
            using var p = d.LoadPage(0);
            var size = p.SizePt;
            int pw = (int)Math.Ceiling(size.Width), ph = (int)Math.Ceiling(size.Height);
            return (p.RenderTile(1.0, 0, 0, pw, ph, true), pw, ph);
        }).GetAwaiter().GetResult();
        PngFile.WriteBgra(png, bgra, w, h);
    }

    [Fact]
    public async Task Sample_Document_SavedAndReopened()
    {
        string src = ParagraphTestData.Sample("doc-en-left").Path;
        var s = await Open(src);
        var page = s.Pages[0].Id;
        var paras = await Paragraphs(s, page, 3);
        Assert.True(paras.Count >= 4);
        // ① 원래 글꼴로 낱말 하나를 고침  ② 앞에 한글을 붙여 대체 글꼴로  ③ ①을 한 번 더 고침(다시 편집)  ④ 문단 하나를 짧게(아래가 비워짐)  ⑤ 마지막 문단은 그대로
        string t1 = ReverseWord(paras[0]);
        var plan1 = await Edit(s, page, paras[0], t1);
        Assert.False(plan1.UsedAppFont);
        var plan2 = await Edit(s, page, paras[1], "문단 앞에 붙인 한글 문장입니다. " + ParagraphLayout.OriginalText(paras[1]));
        Assert.True(plan2.UsedAppFont);
        string t3 = t1.Replace(paras[0].Lines[0].Text.Split(' ')[0], paras[0].Lines[0].Text.Split(' ')[0].ToUpperInvariant());
        var plan3 = await Edit(s, page, paras[0], t3, plan1.Edit);
        string shorter = string.Join(" ", ParagraphLayout.OriginalText(paras[2]).Split(' ').Take(12));
        var plan4 = await Edit(s, page, paras[2], shorter);

        Directory.CreateDirectory(SpikeOut());
        string outPath = Path.Combine(SpikeOut(), "m5-paragraphs-document.pdf");
        await Svc(s).SaveAsync(s, outPath);
        string text = Squash(await TextOf(outPath));
        Assert.Contains(Squash("문단 앞에 붙인 한글 문장입니다."), text);
        Assert.Contains(Squash(t3)[..50], text);
        Assert.Contains(Squash(shorter), text);
        Assert.Equal(0, await OutsideDiff(src, outPath, [Region(paras[0], plan3), Region(paras[1], plan2), Region(paras[2], plan4)]));
        WritePng(outPath, Path.Combine(SpikeOut(), "m5-paragraphs-document.png"));
    }

    [Fact]
    public async Task Sample_Drawing_SavedAndReopened()
    {
        string src = ParagraphTestData.Sample("dwg-rot90").Path;
        var s = await Open(src);
        var page = s.Pages[0].Id;
        var paras = await Paragraphs(s, page, 3);
        var horizontal = paras.First(p => p.Lines[0].Angle == 0);
        var rotated = paras.First(p => p.Lines[0].Angle == 90);
        // 가로 문단: 앞에 한글을 붙여 대체 글꼴로 · 90° 문단: 원래 글꼴로 낱말 하나를 고침
        var p1 = await Edit(s, page, horizontal, "개정 B: " + ParagraphLayout.OriginalText(horizontal));
        var p2 = await Edit(s, page, rotated, ReverseWord(rotated));
        Assert.True(p1.UsedAppFont);
        Directory.CreateDirectory(SpikeOut());
        string outPath = Path.Combine(SpikeOut(), "m5-paragraphs-drawing.pdf");
        await Svc(s).SaveAsync(s, outPath);
        Assert.Contains(Squash("개정 B:"), Squash(await TextOf(outPath)));
        Assert.Equal(0, await OutsideDiff(src, outPath, [Region(horizontal, p1), Region(rotated, p2)]));
        WritePng(outPath, Path.Combine(SpikeOut(), "m5-paragraphs-drawing.png"));
    }
}
