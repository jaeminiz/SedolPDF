using System.Text;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.ContentStream;
using PdfEditor.Document;
using PdfEditor.Document.Saving;
using PdfEditor.TextEdit.FindReplace;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit.Abstractions;

namespace PdfEditor.TextEdit.Tests;

/// <summary>
/// 0.4.0 Task 5: 찾아 바꾸기 저장. 모두 실제 저장 길(세션 → OutputCopyBuilder → PDFsharp 내용 스트림 고쳐 쓰기)로 저장하고 PDFium으로 다시 열어 확인한다.
/// 다른 뷰어(Acrobat·PDF-XChange·Foxit·Chrome)는 자동으로 못 잡는다(사용자 점검).
/// </summary>
public sealed class ReplaceSaveTests(ITestOutputHelper log) : IDisposable
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
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v040-replace-" + Guid.NewGuid().ToString("N"));
    private readonly List<DocumentSession> _sessions = [];

    public void Dispose()
    {
        foreach (var s in _sessions) s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) { Directory.CreateDirectory(_dir); return Path.Combine(_dir, name); }

    private async Task<DocumentSession> Open(string path, string? pw = null)
    {
        var s = await DocumentSession.OpenAsync(_t, path, pw);
        _sessions.Add(s);
        return s;
    }

    private static SaveService Svc(DocumentSession s) => new([], null, new PdfSharpAnnotationPatcher(Font.Value) { SerifFont = () => Catalog.Value.Serif }, s.Assets);

    private static readonly FindOptions OldWhole = new() { Query = DrawingIdPdfFactory.Id, MatchCase = true, WholeWord = true };

    /// <summary>찾기 → 계획 → 실행(명령 한 개). 반환: (찾은 수, 계획 묶음, 명령).</summary>
    private async Task<(FindOutcome Found, ReplaceBatch Batch)> FindAndPlan(DocumentSession s, string newText, FindOptions? opts = null)
    {
        var found = await FindReplaceService.FindAsync(s, opts ?? OldWhole);
        var batch = await FindReplaceService.PlanAsync(s, found, [.. found.Matches], newText, Font.Value, Catalog.Value);
        return (found, batch);
    }

    private async Task<int> CountIn(string pdf, string query, bool whole = true, bool matchCase = true, int page = -1)
    {
        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        var rep = DocumentTextFinder.FindAll(doc, new FindOptions { Query = query, WholeWord = whole, MatchCase = matchCase }, pageIndexes: page < 0 ? null : [page]);
        return rep.TotalMatches;
    }

    private Task<int> PdfiumCount(string pdf, string query, int page = 0) => _t.InvokeAsync(() =>
    {
        using var d = Pdf.Native.PdfDocument.Open(_t, pdf, null);
        using var p = d.LoadPage(page);
        using var tp = p.LoadText();
        return tp.FindAll(query, true, false).Count;
    });

    private Task<(byte[] Px, int W, int H)> Render(string pdf, int pageIndex = 0) => _t.InvokeAsync(() =>
    {
        using var d = Pdf.Native.PdfDocument.Open(_t, pdf, null);
        using var p = d.LoadPage(pageIndex);
        var size = p.SizePt;
        int w = (int)Math.Ceiling(size.Width * 2), h = (int)Math.Ceiling(size.Height * 2);
        return (p.RenderTile(2, 0, 0, w, h, false), w, h);
    });


    /// <summary>바꾼 낱말이 놓인 논리 글줄 전체(뒤 글이 새 글 폭만큼 밀리는 줄) 둘레.</summary>
    private static List<(double L, double B, double R, double T)> LineRegions(ReplaceBatch batch)
    {
        var list = new List<(double, double, double, double)>();
        foreach (var it in batch.Items.Where(i => i.Ok))
        {
            var m = it.Match;
            if (m.Source is not { } src || m.LineIndex < 0) continue;
            var b = TextMatcher.BoundsOf(src.Lines[m.LineIndex].Glyphs.Where(g => !g.Virtual));
            double pad = m.Glyphs[0].Size * 1.5 + it.Plan.NewLengthPt;
            list.Add((b.L - pad, b.B - pad, b.R + pad, b.T + pad));
        }
        return list;
    }
    /// <summary>바꾼 곳 둘레(쪽 공간 AABB)</summary>
    private static List<(double L, double B, double R, double T)> Regions(ReplaceBatch batch, IEnumerable<ReplaceItem>? only = null)
    {
        var list = new List<(double, double, double, double)>();
        foreach (var it in only ?? batch.Items.Where(i => i.Ok))
        {
            var f = it.Match.Glyphs[0]; var l = it.Match.Glyphs[^1];
            double th = f.Angle * Math.PI / 180, dx = Math.Cos(th), dy = Math.Sin(th);
            double oldLen = (l.X1 - f.X0) * dx + (l.Y1 - f.Y0) * dy, len = Math.Max(oldLen, it.Plan.NewLengthPt);
            double nx = -dy * 1.3 * f.Size, ny = dx * 1.3 * f.Size;
            var xs = new[] { f.X0 + nx, f.X0 - nx, f.X0 + dx * len + nx, f.X0 + dx * len - nx };
            var ys = new[] { f.Y0 + ny, f.Y0 - ny, f.Y0 + dy * len + ny, f.Y0 + dy * len - ny };
            list.Add((xs.Min() - 3, ys.Min() - 3, xs.Max() + 3, ys.Max() + 3));
        }
        return list;
    }

    /// <summary>두 그림(배율 2)에서 영역 밖에서 다른 픽셀 수. rot90 = 쪽 /Rotate 90(픽셀 → 쪽 공간: X = py, Y = px).</summary>
    private static int OutsideDiff(byte[] a, byte[] b, int w, int h, IReadOnlyList<(double L, double B, double R, double T)> regions, bool rot90, double pageHeight = 842)
    {
        int diff = 0;
        for (int py = 0; py < h; py++)
            for (int px = 0; px < w; px++)
            {
                int i = (py * w + px) * 4;
                if (Math.Abs(a[i] - b[i]) <= 64 && Math.Abs(a[i + 1] - b[i + 1]) <= 64 && Math.Abs(a[i + 2] - b[i + 2]) <= 64) continue;
                double X = (rot90 ? py : px) / 2.0, Y = rot90 ? px / 2.0 : pageHeight - py / 2.0;
                if (regions.Any(r => X >= r.L && X <= r.R && Y >= r.B && Y <= r.T)) continue;
                diff++;
            }
        return diff;
    }

    private static bool Rot(IdCase c) => c == IdCase.PageRot90;

    // ------------------------------------------------------------------ 합성 표본 전체

    public static IEnumerable<object[]> Cases() => Enum.GetValues<IdCase>().Where(c => c != IdCase.NoToUnicode).Select(c => new object[] { c });

    [Theory, MemberData(nameof(Cases))]
    public async Task Replace_SameLength_OnlyThatWordChanges(IdCase c)
    {
        string src = P($"{c}-same.pdf"); DrawingIdPdfFactory.Create(src, c);
        var s = await Open(src);
        var (found, batch) = await FindAndPlan(s, "H-1052");
        using var _ = found;
        Assert.Equal(3, batch.Items.Count);
        Assert.All(batch.Items, i => Assert.True(i.Ok, i.Refusal.ToString()));
        Assert.All(batch.Items, i => Assert.NotEqual(ReplaceFontKind.App, i.Font));   // 범례가 있어 원래 글꼴로 쓸 수 있다
        var cmd = FindReplaceService.BuildCommand(s, batch);
        Assert.NotNull(cmd);
        await s.Commands.ExecuteAsync(cmd!);
        string saved = P($"{c}-same-out.pdf");
        await Svc(s).SaveAsync(s, saved);

        var (a, w, h) = await Render(src); var (b, _, _) = await Render(saved);
        int outside = OutsideDiff(a, b, w, h, Regions(batch), Rot(c));
        log.WriteLine($"{c}: 영역 밖 다른 픽셀 {outside}");
        Assert.Equal(0, outside);
        Assert.Equal(0, await CountIn(saved, DrawingIdPdfFactory.Id, true, true));   // 옛 번호(완전일치) 0건
        Assert.Equal(3, await CountIn(saved, "H-1052", true, true));
        Assert.Equal(await PdfiumCount(src, DrawingIdPdfFactory.Id) - 3, await PdfiumCount(saved, DrawingIdPdfFactory.Id));   // PDFium 텍스트에도 옛 번호 3곳이 없다(부분 문자열 미끼 H-25011만 남음)
    }

    [Theory]
    [InlineData(IdCase.Whole, "H-26")]
    [InlineData(IdCase.Whole, "H-250123")]
    [InlineData(IdCase.Rot90, "H-26")]
    [InlineData(IdCase.Rot90, "H-250123")]
    [InlineData(IdCase.PageRot90, "H-26")]
    [InlineData(IdCase.PageRot90, "H-250123")]
    [InlineData(IdCase.TjArray, "H-250123")]
    [InlineData(IdCase.PerGlyph, "H-26")]
    [InlineData(IdCase.SimpleChain, "H-250123")]
    public async Task Replace_DifferentLength_RotatedBaselineKept(IdCase c, string newText)
    {
        string src = P($"{c}-{newText}.pdf"); DrawingIdPdfFactory.Create(src, c);
        var s = await Open(src);
        var (found, batch) = await FindAndPlan(s, newText);
        using var _ = found;
        Assert.All(batch.Items, i => Assert.True(i.Ok));
        bool longer = newText.Length > DrawingIdPdfFactory.Id.Length;
        Assert.Equal(0, batch.Items.Count(i => i.Overlap));   // 같은 글 연산 안의 뒤 글(" REV A")은 새 글 폭만큼 같이 밀려 덮이지 않는다(0.4.0)
        await s.Commands.ExecuteAsync(FindReplaceService.BuildCommand(s, batch)!);
        string saved = P($"{c}-{newText}-out.pdf");
        await Svc(s).SaveAsync(s, saved);

        var (a, w, h) = await Render(src); var (b, _, _) = await Render(saved);
        Assert.Equal(0, OutsideDiff(a, b, w, h, [.. Regions(batch), .. LineRegions(batch)], Rot(c)));   // 뒤 글이 밀린 줄은 비교에서 뺀다
        int oldLeft = await CountIn(saved, DrawingIdPdfFactory.Id, true, true);
        Assert.True(oldLeft <= (longer ? 1 : 0), $"옛 번호 {oldLeft}건 남음");   // 길게 바꾸면 문장 속 새 글이 뒤 글과 겹쳐 읽는 순서가 섞일 수 있다(D8 경고 대상)

        // 같은 각도·같은 기준선·같은 크기
        using var oldDoc = PdfReader.Open(src, PdfDocumentOpenMode.Import);
        using var newDoc = PdfReader.Open(saved, PdfDocumentOpenMode.Import);
        var before = found.Matches.Select(m => m.Glyphs[0]).OrderBy(g => g.X0).ThenBy(g => g.Y0).ToList();
        var afterIdx = PageTextIndex.Build(newDoc.Pages[0], 0);
        var after = afterIdx.Find(new FindOptions { Query = newText, MatchCase = true }).Where(m => m.Text == newText).Select(m => m.Glyphs[0]).OrderBy(g => g.X0).ThenBy(g => g.Y0).ToList();
        Assert.Equal(before.Count - (longer ? batch.Items.Count(i => i.Overlap) : 0), after.Count);   // 길게 바꿔 뒤 글과 겹친 곳은 읽는 순서가 섞여 새 번호로 안 읽힐 수 있다
        foreach (var na in after)
        {
            var ob = before.OrderBy(x => Math.Abs(x.X0 - na.X0) + Math.Abs(x.Y0 - na.Y0)).First();   // 가장 가까운 옛 번호
            Assert.Equal(ob.Angle, na.Angle);
            Assert.InRange(Math.Abs(ob.X0 - na.X0) + Math.Abs(ob.Y0 - na.Y0), 0, 0.05);   // 시작점이 같다(같은 기준선)
            Assert.InRange(Math.Abs(ob.Size - na.Size), 0, 0.01);
        }
    }


    /// <summary>
    /// 0.4.0 실제 도면 결함: 새 글이 더 길면 같은 글 연산 안의 뒤따르는 글(" REV A", "-200")이 새 글 폭만큼 같이 밀린다.
    /// 기준본 = 처음부터 새 번호로 만든 같은 문서 — 번호가 든 문장 줄이 기준본과 같아야 한다(저장 파일).
    /// </summary>
    [Theory]
    [InlineData("H-250123")]
    [InlineData("H-26")]
    public async Task Replace_DifferentLength_FollowingTextInTheSameOperatorMovesAlong(string newText)
    {
        string src = P($"shift-{newText}.pdf"); DrawingIdPdfFactory.Create(src, IdCase.Whole);
        string refPath = P($"shift-{newText}-ref.pdf"); DrawingIdPdfFactory.Create(refPath, IdCase.Whole, newText);
        var s = await Open(src);
        var (found, batch) = await FindAndPlan(s, newText);
        using var _ = found;
        Assert.Equal(0, batch.OverlapCount);
        await s.Commands.ExecuteAsync(FindReplaceService.BuildCommand(s, batch)!);
        string saved = P($"shift-{newText}-out.pdf");
        await Svc(s).SaveAsync(s, saved);
        var (a, w, h) = await Render(refPath); var (b, _, _) = await Render(saved);
        // 문장 줄 "REF {번호} REV A"(PDF y 약 280): 기준본과 같은 자리에 같은 모양
        int y0 = (842 - 296) * 2, y1 = (842 - 268) * 2, bad = 0;
        for (int y = y0; y < y1; y++)
            for (int x = 90 * 2; x < 330 * 2; x++)
            {
                int i = (y * w + x) * 4;
                if (Math.Abs(a[i] - b[i]) > 40 || Math.Abs(a[i + 1] - b[i + 1]) > 40 || Math.Abs(a[i + 2] - b[i + 2]) > 40) bad++;
            }
        log.WriteLine($"{newText}: 문장 줄 기준본과 다른 픽셀 {bad}");
        Assert.True(bad < 40, $"문장 줄이 기준본과 다름: {bad} px");
        // 뒤 글도 파일에서 그대로 읽힌다
        Assert.Equal(1, await CountIn(saved, "REV A", true, false));
    }
    [Fact]
    public async Task Replace_OldTextFullyRemoved_FromTheFile()
    {
        string src = P("gone.pdf"); DrawingIdPdfFactory.Create(src, IdCase.Whole);
        var s = await Open(src);
        var (found, batch) = await FindAndPlan(s, "H-1052");
        using var _ = found;
        // 옛 번호의 글 코드열 자체가 파일의 내용 스트림에서 사라졌는지
        var oldCodes = found.Matches.Select(m => string.Concat(m.Glyphs.Select(g => g.RawCode.ToString("X4")))).Distinct().ToList();
        await s.Commands.ExecuteAsync(FindReplaceService.BuildCommand(s, batch)!);
        string saved = P("gone-out.pdf");
        await Svc(s).SaveAsync(s, saved);
        using var doc = PdfReader.Open(saved, PdfDocumentOpenMode.Import);
        string content = Encoding.Latin1.GetString(PageContent.Load(doc.Pages[0]).Data!).ToUpperInvariant();
        using var srcDoc = PdfReader.Open(src, PdfDocumentOpenMode.Import);
        string srcContent = Encoding.Latin1.GetString(PageContent.Load(srcDoc.Pages[0]).Data!).ToUpperInvariant().Replace(" ", "");
        int Count(string hay, string needle) { int n = 0; for (int i = hay.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = hay.IndexOf(needle, i + 1, StringComparison.Ordinal)) n++; return n; }
        // 옛 번호의 글 코드열은 부분 문자열 미끼(H-25011, 한 곳)에만 남는다 — 바꾼 세 곳에서는 파일에서 사라졌다
        foreach (var codes in oldCodes) Assert.Equal(Count(srcContent, codes) - 3, Count(content.Replace(" ", ""), codes));
        Assert.Equal(await CountIn(src, "2501", false, false) - 3, await CountIn(saved, "2501", false, false));   // "2501" 조각도 바꾼 세 곳에서는 사라졌다(미끼 3곳은 그대로)
        Assert.Equal(await PdfiumCount(src, DrawingIdPdfFactory.Id) - 3, await PdfiumCount(saved, DrawingIdPdfFactory.Id));
    }

    [Fact]
    public async Task Replace_GlyphMissingInSubset_FallsBackToAppFont_WithNotice()
    {
        string src = P("subset.pdf"); DrawingIdPdfFactory.Create(src, IdCase.Whole, legend: false);
        var s = await Open(src);
        var (found, batch) = await FindAndPlan(s, "H-2604");   // 6·4는 서브셋에 없다
        using var _ = found;
        Assert.All(batch.Items, i => Assert.True(i.Ok, i.Refusal.ToString()));
        Assert.Equal(3, batch.AppFontCount);   // 알림: 대체 글꼴 3개
        Assert.All(batch.Items, i => Assert.Equal(ReplaceFontKind.App, i.Font));
        await s.Commands.ExecuteAsync(FindReplaceService.BuildCommand(s, batch)!);
        string saved = P("subset-out.pdf");
        await Svc(s).SaveAsync(s, saved);
        var (a, w, h) = await Render(src); var (b, _, _) = await Render(saved);
        Assert.Equal(0, OutsideDiff(a, b, w, h, [.. Regions(batch), .. LineRegions(batch)], false));
        Assert.Equal(0, await CountIn(saved, DrawingIdPdfFactory.Id, true, true));
        // 새 글이 실제로 그려졌다(네모가 아니다): 바꾼 곳에 잉크가 있고 PDFium이 글자를 읽는다
        Assert.True(await PdfiumCount(saved, "2604") >= 1);
    }

    [Fact]
    public async Task ReplaceAll_OneUndoRestores()
    {
        string src = P("undo.pdf"); DrawingIdPdfFactory.Create(src, IdCase.TjArray);
        var s = await Open(src);
        var (found, batch) = await FindAndPlan(s, "H-1052");
        using var _ = found;
        string before = P("undo-before.pdf"); await Svc(s).SaveAsync(s, before);   // 편집 없는 저장(바이트 그대로 복사가 아니어도 모양은 같다)
        await s.Commands.ExecuteAsync(FindReplaceService.BuildCommand(s, batch)!);
        Assert.True(s.Commands.CanUndo);
        await s.Commands.UndoAsync();
        Assert.False(s.Content.HasEdits);   // 실행 취소 한 번에 편집 기록이 모두 사라진다
        string undone = P("undo-after.pdf"); await Svc(s).SaveAsync(s, undone);
        var (a, w, h) = await Render(src); var (b, _, _) = await Render(undone);
        Assert.Equal(0, OutsideDiff(a, b, w, h, [], false));
        Assert.Equal(3, await CountIn(undone, DrawingIdPdfFactory.Id, true, true));
        await s.Commands.RedoAsync();
        Assert.True(s.Content.HasEdits);
    }

    [Fact]
    public async Task Replace_SaveReopenFindAgain_OldIdZero()
    {
        string src = P("reopen.pdf"); DrawingIdPdfFactory.Create(src, IdCase.PerGlyph);
        var s = await Open(src);
        var (found, batch) = await FindAndPlan(s, "H-1052");
        using var _ = found;
        await s.Commands.ExecuteAsync(FindReplaceService.BuildCommand(s, batch)!);
        string saved = P("reopen-out.pdf");
        await Svc(s).SaveAsync(s, saved);

        var s2 = await Open(saved);
        using var oldFound = await FindReplaceService.FindAsync(s2, OldWhole);
        Assert.Empty(oldFound.Matches);
        using var newFound = await FindReplaceService.FindAsync(s2, new FindOptions { Query = "H-1052", MatchCase = true, WholeWord = true });
        Assert.Equal(3, newFound.Matches.Count());
    }

    [Fact]
    public async Task Replace_AlreadyReplaced_AreNotFoundAgainInTheSameSession()
    {
        string src = P("again.pdf"); DrawingIdPdfFactory.Create(src, IdCase.Whole);
        var s = await Open(src);
        var (found, batch) = await FindAndPlan(s, "H-1052");
        using var _ = found;
        await s.Commands.ExecuteAsync(FindReplaceService.BuildCommand(s, batch)!);
        using var again = await FindReplaceService.FindAsync(s, OldWhole);
        Assert.Empty(again.Matches);   // 이미 바꾼 낱말은 후보에서 빠진다(같은 글을 두 번 바꾸지 않는다)
        // 화면: 원본 글 객체를 끄고 조각을 넣었다
        var replay = await _t.InvokeAsync(() => s.GetReplay(s.Pages[0].Id));
        Assert.NotEmpty(replay.Inserted);
    }

    [Fact]
    public async Task Replace_TwoWordsInOneOperator_InTwoBatches_BothReplaced()
    {
        string src = P("two.pdf");
        FindEngineTests.WriteRaw(src, ["BT /F1 10 Tf 100 700 Td (see H-2501 and also H-2501 ok) Tj ET"]);
        var s = await Open(src);
        var opt = OldWhole;
        using var f1 = await FindReplaceService.FindAsync(s, opt with { Region = (0, 0, 175, 842) });   // 왼쪽 낱말만
        var b1 = await FindReplaceService.PlanAsync(s, f1, [.. f1.Matches], "H-1052", Font.Value, Catalog.Value);
        Assert.Single(b1.Items);
        await s.Commands.ExecuteAsync(FindReplaceService.BuildCommand(s, b1)!);
        using var f2 = await FindReplaceService.FindAsync(s, opt);
        Assert.Single(f2.Matches);   // 나머지 한 곳만 남는다
        var b2 = await FindReplaceService.PlanAsync(s, f2, [.. f2.Matches], "H-2604", Font.Value, Catalog.Value);
        await s.Commands.ExecuteAsync(FindReplaceService.BuildCommand(s, b2)!);
        string saved = P("two-out.pdf");
        await Svc(s).SaveAsync(s, saved);
        Assert.Equal(0, await CountIn(saved, DrawingIdPdfFactory.Id, true, true));
        Assert.Equal(1, await CountIn(saved, "H-1052", true, true));
        Assert.Equal(1, await CountIn(saved, "H-2604", true, true));
        Assert.Equal(1, await CountIn(saved, "see", true, true));
    }

    [Fact]
    public async Task Replace_Form_ClipText_And_Encrypted_AreRefusedNotSilentlySkipped()
    {
        // 폼 XObject 안 글 → InForm
        string form = P("form.pdf");
        using (var doc = new PdfSharp.Pdf.PdfDocument())
        {
            var page = doc.AddPage();
            var res = new PdfDictionary(doc); page.Elements["/Resources"] = res;
            var fonts = new PdfDictionary(doc); res.Elements["/Font"] = fonts;
            var f = new PdfDictionary(doc);
            f.Elements["/Type"] = new PdfName("/Font"); f.Elements["/Subtype"] = new PdfName("/Type1"); f.Elements["/BaseFont"] = new PdfName("/Helvetica");
            doc.Internals.AddObject(f); fonts.Elements["/F1"] = f.Reference!;
            var fm = new PdfDictionary(doc);
            fm.Elements["/Type"] = new PdfName("/XObject"); fm.Elements["/Subtype"] = new PdfName("/Form");
            fm.Elements["/BBox"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(300), new PdfInteger(100));
            var fres = new PdfDictionary(doc); var ff = new PdfDictionary(doc); ff.Elements["/F1"] = f.Reference!; fres.Elements["/Font"] = ff; fm.Elements["/Resources"] = fres;
            doc.Internals.AddObject(fm);
            fm.CreateStream(Encoding.Latin1.GetBytes("BT /F1 12 Tf 10 50 Td (H-2501) Tj ET"));
            var xo = new PdfDictionary(doc); xo.Elements["/Fm0"] = fm.Reference!; res.Elements["/XObject"] = xo;
            page.Contents.AppendContent().CreateStream(Encoding.Latin1.GetBytes("q 1 0 0 1 200 300 cm /Fm0 Do Q BT /F1 10 Tf 4 Tr 100 700 Td (H-2501) Tj ET"));
            doc.Save(form);
        }
        var s = await Open(form);
        var (found, batch) = await FindAndPlan(s, "H-1052");
        using var _ = found;
        Assert.Equal(2, batch.Items.Count);
        Assert.Contains(batch.Items, i => i.Refusal == WordRefusal.InForm);
        Assert.Contains(batch.Items, i => i.Refusal == WordRefusal.ClipText);
        Assert.Equal(2, batch.RefusedCount);
        Assert.Equal([1], batch.RefusedPages);
        Assert.Null(FindReplaceService.BuildCommand(s, batch));   // 바꿀 것이 없으면 명령이 없다

        // 암호 문서 → Encrypted (M4x U-B와 같은 정책)
        string enc = P("enc.pdf");
        PdfEditor.TestFixtures.TestPdfFactory.CreateEncrypted(enc, 1, 595, 842, "pw");
        // 암호 PDF에는 번호 글이 없으니 같은 내용으로 새로 만들 수 없다 — 대신 정책 함수와 계획기의 거절 경로를 직접 확인한다.
        var s2 = await Open(enc, "pw");
        Assert.False(SavePolicy.CanPatch(s2));
    }

    [Fact]
    public async Task Replace_OriginalNotChangedWhenNothingChosen()
    {
        string src = P("noop.pdf"); DrawingIdPdfFactory.Create(src, IdCase.Whole);
        var s = await Open(src);
        var (found, batch) = await FindAndPlan(s, "H-1052");
        using var _ = found;
        Assert.Null(FindReplaceService.BuildCommand(s, batch, _ => false));   // 항목을 모두 빼면 명령이 없고 파일은 안 바뀐다
        Assert.False(s.Content.HasEdits);
        Assert.False(s.IsDirty);
    }

    // ------------------------------------------------------------------ 실제 PDF(private/, 수치만 — 파일명·내용은 기록하지 않는다)

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replace_RealPdfSamples_ZeroPixelOutside_OldGone_OneUndo(bool shiftDigits)
    {
        string? priv = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "private"))) { priv = Path.Combine(d.FullName, "private"); break; }
        if (priv is null) return;   // 실제 PDF는 저장소에 없다(private/, 커밋 금지) — 있을 때만
        var sb = new StringBuilder("== Task 5 실제 PDF 표본 바꾸기 (앞 5쪽, 가장 많이 나온 숫자 든 낱말을 글자 순서를 뒤집은 글(shiftDigits면 숫자를 +3씩 바꾼 새 번호)로 바꿈 — 수치만)\n");
        int n = 0, totalReplaced = 0;
        foreach (var f in Directory.EnumerateFiles(priv, "*.pdf").Take(4))
        {
            n++;
            string copy = P($"real{n}.pdf"); File.Copy(f, copy, true);
            var s = await Open(copy);
            var pages = s.Pages.Take(5).Select(p => p.Id).ToList();
            // 가장 많이 나온 낱말 고르기(숫자 포함, 4~12자)
            using var probe = await FindReplaceService.FindAsync(s, new FindOptions { Query = "-" }, pages);
            var tokens = new Dictionary<string, int>();
            using (var doc = PdfReader.Open(copy, PdfDocumentOpenMode.Import))
                for (int i = 0; i < Math.Min(5, doc.PageCount); i++)
                    foreach (var ln in PageTextIndex.Build(doc.Pages[i], i).Lines)
                        foreach (var tk in ln.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                            if (tk.Length is >= 4 and <= 12 && tk.Any(char.IsDigit) && tk.All(c => c < 128)) tokens[tk] = tokens.GetValueOrDefault(tk) + 1;
            var top = tokens.OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (top.Key is null || top.Value < 2) { sb.AppendLine($"표본#{n}: 반복 낱말 없음(건너뜀)"); continue; }
            string newText = shiftDigits ? new(top.Key.Select(c => char.IsDigit(c) ? (char)('0' + (c - '0' + 3) % 10) : c).ToArray()) : new(top.Key.Reverse().ToArray());
            if (newText == top.Key) { sb.AppendLine($"표본#{n}: 뒤집어도 같은 낱말(건너뜀)"); continue; }
            var opts = new FindOptions { Query = top.Key, MatchCase = true, WholeWord = true };
            using var found = await FindReplaceService.FindAsync(s, opts, pages);
            var batch = await FindReplaceService.PlanAsync(s, found, [.. found.Matches], newText, Font.Value, Catalog.Value);
            var cmd = FindReplaceService.BuildCommand(s, batch);
            if (cmd is null) { sb.AppendLine($"표본#{n}: 바꿀 수 있는 곳 0(못 바꾼 {batch.RefusedCount})"); continue; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await s.Commands.ExecuteAsync(cmd);
            string saved = P($"real{n}-out.pdf");
            await Svc(s).SaveAsync(s, saved);
            double saveMs = sw.Elapsed.TotalMilliseconds;
            totalReplaced += batch.OkCount;

            int outside = 0;
            foreach (var pg in batch.Items.Where(i => i.Ok).Select(i => i.Match.PageIndex).Distinct())
            {
                var (a, w, h, rot, w0, h0) = await RenderRot(copy, pg); var (b, _, _, _, _, _) = await RenderRot(saved, pg);
                var regs = Regions(batch, batch.Items.Where(i => i.Ok && i.Match.PageIndex == pg));
                for (int py = 0; py < h; py++)
                    for (int px = 0; px < w; px++)
                    {
                        int i = (py * w + px) * 4;
                        if (Math.Abs(a[i] - b[i]) <= 64 && Math.Abs(a[i + 1] - b[i + 1]) <= 64 && Math.Abs(a[i + 2] - b[i + 2]) <= 64) continue;
                        var (X, Y) = rot switch { 1 => (py / 2.0, px / 2.0), 2 => (w0 - px / 2.0, py / 2.0), 3 => (w0 - py / 2.0, h0 - px / 2.0), _ => (px / 2.0, h0 - py / 2.0) };
                        if (regs.Any(r => X >= r.L && X <= r.R && Y >= r.B && Y <= r.T)) continue;
                        outside++;
                    }
            }
            using var after = await FindReplaceService.FindAsync(await Open(saved), opts, null);
            int oldLeft = after.Matches.Count(m => m.PageIndex < 5);
            sb.AppendLine($"표본#{n}: 낱말 길이 {top.Key.Length}, 후보 {batch.Items.Count}, 바꿈 {batch.OkCount}(앱 글꼴 {batch.AppFontCount}, 겹침 경고 {batch.OverlapCount}), 못 바꾼 {batch.RefusedCount}, 영역 밖 다른 픽셀 {outside}, 저장 뒤 옛 낱말 {oldLeft}(앞 5쪽), 바꾸기+저장 {saveMs:0} ms");
            Assert.Equal(0, outside);
            Assert.True(oldLeft <= batch.OverlapCount, $"표본#{n}: 옛 낱말 {oldLeft}건 남음");
            // 실행 취소 한 번
            await s.Commands.UndoAsync();
            Assert.False(s.Content.HasEdits);
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), shiftDigits ? "v040-task5-real-digits.txt" : "v040-task5-real.txt"), sb.ToString());
        Assert.True(totalReplaced > 0, "실제 PDF에서 바꾼 곳이 하나도 없다");
    }

    private Task<(byte[] Px, int W, int H, int Rot, double W0, double H0)> RenderRot(string pdf, int pageIndex) => _t.InvokeAsync(() =>
    {
        using var d = Pdf.Native.PdfDocument.Open(_t, pdf, null);
        using var p = d.LoadPage(pageIndex);
        var size = p.SizePt;
        int rot = p.Rotation & 3;
        int w = (int)Math.Ceiling(size.Width * 2), h = (int)Math.Ceiling(size.Height * 2);
        bool odd = rot % 2 != 0;
        return (p.RenderTile(2, 0, 0, w, h, false), w, h, rot, (double)(odd ? size.Height : size.Width), (double)(odd ? size.Width : size.Height));
    });

    [Fact]
    public async Task Replace_EncryptedDocument_IsRefused_NotSilentlyDropped()
    {
        string plain = P("enc-src.pdf");
        FindEngineTests.WriteRaw(plain, ["BT /F1 10 Tf 100 700 Td (H-2501) Tj ET"]);
        string enc = P("enc.pdf");
        using (var doc = PdfReader.Open(plain, PdfDocumentOpenMode.Modify))
        {
            doc.SecuritySettings.UserPassword = "pw";
            doc.SecuritySettings.OwnerPassword = "owner";
            doc.Save(enc);
        }
        var s = await Open(enc, "pw");
        var (found, batch) = await FindAndPlan(s, "H-1052");
        using var _ = found;
        Assert.Single(batch.Items);   // 찾을 수는 있다
        Assert.Equal(WordRefusal.Encrypted, batch.Items[0].Refusal);   // 바꾸기는 막는다(M4x U-B와 같은 정책) — 조용히 건너뛰지 않고 이유와 함께 센다
        Assert.Null(FindReplaceService.BuildCommand(s, batch));
    }
}
