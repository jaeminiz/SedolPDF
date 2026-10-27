using System.Diagnostics;
using System.Text;
using PdfEditor.TextEdit.FindReplace;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfEditor.TextEdit.Tests;

/// <summary>0.4.0 Task 4: 쪽 내용 해석 기반 찾기 엔진. 합성 표본의 정답표(통째·TJ 쪼갬·글자마다·90°·쪽 회전·비임베드 글꼴)와 실제 PDF 표본(private/, 수치만).</summary>
public sealed class FindEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v040-find-" + Guid.NewGuid().ToString("N"));
    public FindEngineTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string P(string n) => Path.Combine(_dir, n);

    // 정답: (대소문자 구분+완전일치) 3 · (무시+부분일치) 5 · (무시+완전일치) 4
    private static readonly (string Name, FindOptions Opt, int Truth)[] Modes =
    [
        ("case+whole", new FindOptions { Query = DrawingIdPdfFactory.Id, MatchCase = true, WholeWord = true }, 3),
        ("nocase+substring", new FindOptions { Query = DrawingIdPdfFactory.Id }, 5),
        ("nocase+whole", new FindOptions { Query = DrawingIdPdfFactory.Id, WholeWord = true }, 4),
    ];

    private static PageTextIndex Index(string path, int page = 0)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return PageTextIndex.Build(doc.Pages[page], page);
    }

    private void AssertTruth(IdCase c)
    {
        string path = P($"{c}.pdf");
        DrawingIdPdfFactory.Create(path, c);
        var idx = Index(path);
        Assert.True(idx.Walk.Ok, idx.Walk.Failure);
        foreach (var (name, opt, truth) in Modes)
        {
            var found = idx.Find(opt);
            Assert.True(truth == found.Count, $"{c} {name}: {found.Count}/{truth}");
            Assert.All(found, m =>
            {
                Assert.Equal(DrawingIdPdfFactory.Id, m.Text, ignoreCase: true);
                Assert.NotEmpty(m.Spans);
                Assert.Equal(m.Text.Length, m.Glyphs.Sum(g => g.Text.Length));   // 글자 범위가 찾은 글과 같은 글자 수
                Assert.Equal(m.Text.Length, m.Spans.Sum(s => s.CodeCount));
            });
        }
    }

    [Fact] public void Find_WholeOperator() => AssertTruth(IdCase.Whole);
    [Fact] public void Find_SplitAcrossOperators_FindsWhole() => AssertTruth(IdCase.TjArray);
    [Fact] public void Find_PerGlyphOperators() => AssertTruth(IdCase.PerGlyph);
    [Fact] public void Find_RotatedTitleBlock90() => AssertTruth(IdCase.Rot90);
    [Fact] public void Find_PageRotate90_WithRotatedText() => AssertTruth(IdCase.PageRot90);
    [Fact] public void Find_UnembeddedSimpleFont_TjChain() => AssertTruth(IdCase.SimpleChain);
    [Fact] public void Find_UnembeddedSimpleFont_TjArray() => AssertTruth(IdCase.SimpleTj);

    [Fact]
    public void Find_RotatedMatches_ReportAngleAndExcludeWhenAsked()
    {
        string path = P("rot.pdf");
        DrawingIdPdfFactory.Create(path, IdCase.Rot90);
        var idx = Index(path);
        var all = idx.Find(Modes[0].Opt);
        Assert.Equal(3, all.Count);
        Assert.Single(all, m => m.RotationDeg % 180 == 90);   // 표제란 회전 번호 한 곳
        var horizontalOnly = idx.Find(Modes[0].Opt with { IncludeRotated = false });
        Assert.Equal(2, horizontalOnly.Count);
        Assert.All(horizontalOnly, m => Assert.Equal(0, m.RotationDeg));
    }

    [Fact]
    public void Find_MatchCase_WholeWord_HyphenAsWordChar()
    {
        string path = P("raw1.pdf");
        WriteRaw(path, ["BT /F1 10 Tf 100 700 Td (H-2501) Tj ET", "BT /F1 10 Tf 100 680 Td (X H-2501-A Y) Tj ET", "BT /F1 10 Tf 100 660 Td (see H-2501.) Tj ET", "BT /F1 10 Tf 100 640 Td (A.12 B.12x) Tj ET"]);
        var idx = Index(path);
        // 하이픈을 낱말 글자로 보면(기본) H-2501-A 속 H-2501은 낱말이 아니다. 문장 끝 마침표는 낱말 글자가 아니다.
        Assert.Equal(2, idx.Find(new FindOptions { Query = "H-2501", WholeWord = true }).Count);
        // 끄면 H-2501-A 속 H-2501도 낱말이다.
        Assert.Equal(3, idx.Find(new FindOptions { Query = "H-2501", WholeWord = true, DrawingIdWordChars = false }).Count);
        Assert.Single(idx.Find(new FindOptions { Query = "A.12", WholeWord = true }));
        Assert.Empty(idx.Find(new FindOptions { Query = "B.12", WholeWord = true }));   // 뒤에 x가 붙음
        Assert.Empty(idx.Find(new FindOptions { Query = "h-2501", MatchCase = true }));
        Assert.Equal(3, idx.Find(new FindOptions { Query = "h-2501" }).Count);
    }

    [Fact]
    public void Find_HiddenTextExcluded()
    {
        string path = P("hidden.pdf");
        WriteRaw(path, ["BT /F1 10 Tf 100 700 Td (H-2501) Tj ET", "BT /F1 10 Tf 3 Tr 100 680 Td (H-2501) Tj ET", "BT /F1 10 Tf 0 Tr 100 660 Td (H-2501) Tj ET"]);
        var found = Index(path).Find(new FindOptions { Query = "H-2501" });
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Find_GraphicsStateStack_RestoresTextState()
    {
        // q … Q 안에서 바꾼 자간(Tc)·글꼴 크기가 Q 뒤에는 되돌아와야 위치가 맞고 한 줄로 이어진다.
        string path = P("gs.pdf");
        WriteRaw(path, ["BT /F1 10 Tf 100 700 Td (H-) Tj q 5 Tc /F1 14 Tf (xx) Tj Q (2501) Tj ET"]);
        var idx = Index(path);
        var lines = idx.Lines;
        // Q 뒤 (2501)은 Tc 0·크기 10으로 되돌아와 있다
        var g = idx.Walk.Glyphs.Where(x => x.Text == "2" && x.FontSize == 10).ToList();
        Assert.Single(g);
        Assert.Equal(0, g[0].Tc);
    }

    [Fact]
    public void Find_NoToUnicode_ReportsUnsearchable()
    {
        string path = P("nou.pdf");
        DrawingIdPdfFactory.Create(path, IdCase.NoToUnicode);
        var idx = Index(path);
        Assert.All(Modes, m => Assert.Empty(idx.Find(m.Opt)));
        Assert.True(idx.Walk.UnsearchableOps > 0);
        Assert.Equal(idx.Walk.ShowOps, idx.Walk.UnsearchableOps);
        Assert.NotEmpty(idx.Walk.UnsearchableByFont);

        var report = DocumentTextFinder.FindAll(PdfReader.Open(path, PdfDocumentOpenMode.Import), Modes[0].Opt);
        Assert.Equal(0, report.TotalMatches);
        Assert.Equal([1], report.PagesWithUnsearchable);   // 조용히 건너뛰지 않고 "찾지 못한 쪽"으로 알린다
        Assert.True(report.UnsearchableOps > 0);
    }

    [Fact]
    public void Find_FormXObject_IsFoundButMarkedInForm()
    {
        string path = P("form.pdf");
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            var res = new PdfDictionary(doc); page.Elements["/Resources"] = res;
            var fonts = new PdfDictionary(doc); res.Elements["/Font"] = fonts;
            var f = new PdfDictionary(doc);
            f.Elements["/Type"] = new PdfName("/Font"); f.Elements["/Subtype"] = new PdfName("/Type1"); f.Elements["/BaseFont"] = new PdfName("/Helvetica");
            doc.Internals.AddObject(f); fonts.Elements["/F1"] = f.Reference!;
            var form = new PdfDictionary(doc);
            form.Elements["/Type"] = new PdfName("/XObject"); form.Elements["/Subtype"] = new PdfName("/Form");
            var bbox = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(300), new PdfInteger(100)); form.Elements["/BBox"] = bbox;
            var fres = new PdfDictionary(doc); var ffonts = new PdfDictionary(doc); ffonts.Elements["/F1"] = f.Reference!; fres.Elements["/Font"] = ffonts; form.Elements["/Resources"] = fres;
            doc.Internals.AddObject(form);
            form.CreateStream(Encoding.Latin1.GetBytes("BT /F1 12 Tf 10 50 Td (HULL H-2501) Tj ET"));
            var xo = new PdfDictionary(doc); xo.Elements["/Fm0"] = form.Reference!; res.Elements["/XObject"] = xo;
            page.Contents.AppendContent().CreateStream(Encoding.Latin1.GetBytes("BT /F1 10 Tf 100 700 Td (H-2501) Tj ET q 1 0 0 1 200 300 cm /Fm0 Do Q"));
            doc.Save(path);
        }
        var found = Index(path).Find(new FindOptions { Query = "H-2501", WholeWord = true });
        Assert.Equal(2, found.Count);
        Assert.Single(found, m => m.InForm);
        var inForm = found.Single(m => m.InForm);
        Assert.InRange(inForm.Bounds.L, 200, 300);   // 폼 행렬(cm)이 적용된 쪽 공간 위치
        Assert.InRange(inForm.Bounds.B, 330, 360);
        Assert.False(found.Single(m => !m.InForm).InForm);
    }

    [Fact]
    public void Find_Region_FiltersByCenter()
    {
        string path = P("region.pdf");
        WriteRaw(path, ["BT /F1 10 Tf 100 700 Td (H-2501) Tj ET", "BT /F1 10 Tf 100 300 Td (H-2501) Tj ET"]);
        var idx = Index(path);
        Assert.Equal(2, idx.Find(new FindOptions { Query = "H-2501" }).Count);
        var top = idx.Find(new FindOptions { Query = "H-2501", Region = (0, 500, 595, 842) });
        Assert.Single(top);
        Assert.InRange(top[0].Bounds.B, 690, 710);
    }

    [Fact]
    public void Find_Results_AreInReadingOrder_TopToBottom()
    {
        string path = P("order.pdf");
        WriteRaw(path, ["BT /F1 10 Tf 100 300 Td (H-2501) Tj ET", "BT /F1 10 Tf 100 700 Td (H-2501) Tj ET", "BT /F1 10 Tf 300 700 Td (H-2501) Tj ET"]);
        var found = Index(path).Find(new FindOptions { Query = "H-2501" });
        Assert.Equal([(700.0, 100.0), (700.0, 300.0), (300.0, 100.0)], found.Select(m => (Math.Round(m.Glyphs[0].Y0), Math.Round(m.Glyphs[0].X0))).ToArray());
    }

    [Fact]
    public void Find_Synthetic5000Lines_IsFast()
    {
        string path = P("many.pdf");
        PdfEditor.TestFixtures.ContentPdfFactory.CreateManyTextLines(path, 5000);
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        PageTextIndex.Build(doc.Pages[0], 0);   // 첫 호출 JIT 제외
        double best = double.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            var idx = PageTextIndex.Build(doc.Pages[0], 0);
            var found = idx.Find(new FindOptions { Query = "1" });
            best = Math.Min(best, sw.Elapsed.TotalSeconds);
            Assert.True(idx.Lines.Count >= 4000);
        }
        Assert.True(best <= 0.15, $"5,000줄 쪽 해석+찾기 {best * 1000:0} ms (목표 150 ms)");
    }

    [Fact]
    public void Find_WholeDocument_IsCancellableAndReportsProgress()
    {
        string path = P("doc.pdf");
        using (var doc = new PdfDocument())
        {
            for (int i = 0; i < 6; i++) { var pg = doc.AddPage(); pg.Contents.AppendContent().CreateStream(Encoding.Latin1.GetBytes("BT /F1 10 Tf 100 700 Td (H-2501) Tj ET")); AddHelvetica(doc, pg); }
            doc.Save(path);
        }
        using var d = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var progress = new List<int>();
        var report = DocumentTextFinder.FindAll(d, new FindOptions { Query = "H-2501" }, progress: new Progress<int>(progress.Add));
        Assert.Equal(6, report.TotalMatches);
        Assert.Equal(Enumerable.Range(0, 6), report.Pages.Select(p => p.PageIndex));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => DocumentTextFinder.FindAll(d, new FindOptions { Query = "H-2501" }, ct: cts.Token));
        var some = DocumentTextFinder.FindAll(d, new FindOptions { Query = "H-2501" }, pageIndexes: [1, 3]);
        Assert.Equal([1, 3], some.Pages.Select(p => p.PageIndex));
    }


    [Fact]
    public async Task Find_PdfiumCrossCheck()
    {
        // 교차 확인(0.4.0 확정): PDFium FindAll은 쪼개진 하이픈 낱말을 놓칠 수 있어 합격 기준은 합성 정답표이고, 여기서는 합성 표본에서 PDFium과 같은 개수인지 본다.
        using var t = new Pdf.Native.PdfiumThread();
        foreach (var c in Enum.GetValues<IdCase>().Where(x => x != IdCase.NoToUnicode))
        {
            string path = P($"x-{c}.pdf");
            DrawingIdPdfFactory.Create(path, c);
            var idx = Index(path);
            foreach (var (name, opt, truth) in Modes)
            {
                int pdfium = await t.InvokeAsync(() =>
                {
                    using var d = Pdf.Native.PdfDocument.Open(t, path, null);
                    using var pg = d.LoadPage(0);
                    using var tp = pg.LoadText();
                    return tp.FindAll(opt.Query, opt.MatchCase, opt.WholeWord).Count;
                });
                Assert.True(pdfium == idx.Find(opt).Count, $"{c} {name}: ours {idx.Find(opt).Count}, pdfium {pdfium}");
            }
        }
    }

    // ------------------------------------------------------------------ 도우미

    private static void AddHelvetica(PdfDocument doc, PdfPage page)
    {
        var res = new PdfDictionary(doc); page.Elements["/Resources"] = res;
        var fonts = new PdfDictionary(doc); res.Elements["/Font"] = fonts;
        var f = new PdfDictionary(doc);
        f.Elements["/Type"] = new PdfName("/Font"); f.Elements["/Subtype"] = new PdfName("/Type1"); f.Elements["/BaseFont"] = new PdfName("/Helvetica");
        f.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        doc.Internals.AddObject(f); fonts.Elements["/F1"] = f.Reference!;
    }

    /// <summary>비임베드 Helvetica 쪽 하나(A4)에 내용 줄들을 쓴다.</summary>
    internal static void WriteRaw(string path, IEnumerable<string> lines)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = PdfSharp.Drawing.XUnit.FromPoint(595); page.Height = PdfSharp.Drawing.XUnit.FromPoint(842);
        AddHelvetica(doc, page);
        page.Contents.AppendContent().CreateStream(Encoding.Latin1.GetBytes(string.Join('\n', lines)));
        doc.Save(path);
    }

    [Fact]
    public void Find_RealPdfSamples_NumbersOnly()
    {
        string? priv = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "private"))) { priv = Path.Combine(d.FullName, "private"); break; }
        if (priv is null) return;   // 실제 PDF는 저장소에 없다(private/, 커밋 금지) — 있을 때만
        var sb = new StringBuilder("== Task 4 실제 PDF 표본 찾기 (앞 5쪽, 수치만)\n");
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(priv, "*.pdf").Take(4))
        {
            n++;
            using var doc = PdfReader.Open(f, PdfDocumentOpenMode.Import);
            var pages = Enumerable.Range(0, Math.Min(5, doc.PageCount)).ToList();
            var sw = Stopwatch.StartNew();
            var rep = DocumentTextFinder.FindAll(doc, new FindOptions { Query = "-" }, pageIndexes: pages);
            sb.AppendLine($"표본#{n}: 쪽 {pages.Count}, \"-\" 포함 {rep.TotalMatches}곳, 찾을 수 없는 글 연산 {rep.UnsearchableOps}, 실패한 쪽 {rep.FailedPages.Count}, {sw.Elapsed.TotalMilliseconds:0} ms");
            Assert.Empty(rep.FailedPages);
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "v040-task4-real.txt"), sb.ToString());
    }
}
