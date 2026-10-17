using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Exchange.Summary;

namespace PdfEditor.Exchange.Tests;

/// <summary>다른 시험과 동시에 돌지 않는다: PDFium은 한 번에 한 스레드에서만 써야 하고, 메모리 시험은 프로세스 전체 관리 메모리를 재기 때문.</summary>
[CollectionDefinition("SummarySerial", DisableParallelization = true)]
public sealed class SummarySerialCollection;

[Collection("SummarySerial")]
public sealed class SummaryPdfBuilderTests : IDisposable
{
    private static readonly SummaryText Ko = new(
        CreatedFormat: "만든 시각: {0}",
        CountFormat: "주석 {0}개",
        PageHeadingFormat: "{0}쪽",
        PageHeadingContinuedFormat: "{0}쪽 (이어서)",
        MissingGlyphsNoticeFormat: "글꼴에 없는 글자 {0}개: {1}");

    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 9, 30, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sedol-summary-" + Guid.NewGuid().ToString("N"));
    private readonly AppFont _font = AppFont.Load(FontPath());

    public SummaryPdfBuilderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _font.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string FontPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var f = Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf");
            if (File.Exists(f)) return f;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("assets/fonts/NotoSansKR-Regular.otf");
    }

    private static byte[] Gray(int w, int h)
    {
        var b = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { b[i * 4] = 220; b[i * 4 + 1] = 230; b[i * 4 + 2] = 240; b[i * 4 + 3] = 255; }
        return b;
    }

    private static SummaryEntry E(int n, string kind, string? contents, string? author = "Kim", params SummaryEntry[] replies)
        => new(n, kind, author, T0, contents, replies, new PdfRectF(20, 30, 120, 70));

    private static SummaryPage P(int number, params SummaryEntry[] entries) => new(number, Gray(200, 280), 200, 280, entries);

    private static SummaryInput Input(string name, params SummaryPage[] pages) => new(name, T0, pages) { Text = Ko };

    private string Out(string name = "out.pdf") => Path.Combine(_dir, name);

    private static IReadOnlyList<string> Texts(string path)
    {
        using var t = new PdfiumThread();
        return t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(t, path);
            var r = new List<string>();
            for (int i = 0; i < doc.PageCount; i++)
            {
                using var page = doc.LoadPage(i);
                using var tp = page.LoadText();
                r.Add(tp.GetText(0, tp.CharCount));
            }
            return (IReadOnlyList<string>)r;
        }).GetAwaiter().GetResult();
    }

    private static string Squash(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    [Fact]
    public void Summary_FirstPageHeaderAndCount()
    {
        var input = Input("검토 도면.pdf",
            P(1, E(1, "형광펜", "첫째 메모"), E(2, "사각형", "둘째")),
            P(3, E(3, "스티커 메모", "셋째", "Lee", E(0, "답글", "답"))));
        var r = SummaryPdfBuilder.Build(input, _font, Out(), CancellationToken.None);

        var pages = Texts(Out());
        string first = Squash(pages[0]);
        Assert.Contains(Squash("검토 도면.pdf"), first);
        Assert.Contains("주석4개", first);   // 최상위 3 + 답글 1
        Assert.Contains("2026-10-18", first);
        Assert.Equal(4, r.EntryCount);
        Assert.Equal(pages.Count, r.SummaryPageCount);
        Assert.Empty(r.MissingCodepoints);
    }

    [Fact]
    public void Summary_PagesWithoutCommentsSkipped()
    {
        var input = Input("a.pdf",
            P(1, E(1, "형광펜", "가나다")),
            new SummaryPage(2, Gray(200, 280), 200, 280, []),
            P(7, E(2, "사각형", "라마바")));
        SummaryPdfBuilder.Build(input, _font, Out(), CancellationToken.None);

        string all = Squash(string.Concat(Texts(Out())));
        Assert.Contains("1쪽", all);
        Assert.Contains("7쪽", all);
        Assert.DoesNotContain("2쪽", all);
        Assert.Contains("주석2개", all);
    }

    [Fact]
    public void Summary_LongCommentFlowsToNextPage()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 400; i++) sb.Append("긴 메모 문장 ").Append(i).Append("번째입니다. ");
        string longText = sb.ToString();
        var input = Input("긴.pdf", P(5, E(1, "텍스트 상자", longText), E(2, "연필", "끝 메모")));
        var r = SummaryPdfBuilder.Build(input, _font, Out(), CancellationToken.None);

        var pages = Texts(Out());
        Assert.True(pages.Count >= 3, "pages=" + pages.Count);
        Assert.Contains("5쪽(이어서)", Squash(pages[1]));
        Assert.DoesNotContain("5쪽(이어서)", Squash(pages[0]));
        // 모든 글이 어딘가에 빠짐없이 들어 있다 (줄바꿈·공백은 무시)
        string all = Squash(string.Concat(pages)).Replace("5쪽(이어서)", "");   // 이어지는 쪽의 반복 쪽 머리는 뺀다
        Assert.Contains(Squash(longText), all);
        Assert.Contains("끝메모", all);
        Assert.Equal(pages.Count, r.SummaryPageCount);
    }

    [Fact]
    public void Summary_KoreanTextExtractable()
    {
        const string text = "한글 주석 내용 ABC 123 입니다";
        var input = Input("한글.pdf", P(1, E(1, "스티커 메모", text, "김재민")));
        SummaryPdfBuilder.Build(input, _font, Out(), CancellationToken.None);

        string all = Squash(string.Concat(Texts(Out())));
        Assert.Contains(Squash(text), all);
        Assert.Contains("김재민", all);
        Assert.Contains("스티커메모", all);
    }

    [Fact]
    public async Task Summary_RepliesIndented()
    {
        var input = Input("r.pdf", P(1, E(1, "스티커 메모", "PARENTBODY", "Kim", E(0, "답글", "REPLYBODY", "Lee"))));
        SummaryPdfBuilder.Build(input, _font, Out(), CancellationToken.None);

        using var t = new PdfiumThread();
        (float parentX, float replyX) = await t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(t, Out());
            using var page = doc.LoadPage(0);
            using var tp = page.LoadText();
            string text = tp.GetText(0, tp.CharCount);
            float X(string q)
            {
                int i = text.IndexOf(q, StringComparison.Ordinal);
                Assert.True(i >= 0, "not found: " + q + " in " + text);
                return tp.GetRects(i, 1)[0].Left;
            }
            return (X("PARENTBODY"), X("REPLYBODY"));
        });
        Assert.True(replyX > parentX + 8, $"parent={parentX} reply={replyX}");
    }

    [Fact]
    public void Summary_Cancel_NoFileLeft()
    {
        var input = Input("c.pdf", P(1, E(1, "a", "x")), P(2, E(2, "a", "y")), P(3, E(3, "a", "z")));

        // 처음부터 취소
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => SummaryPdfBuilder.Build(input, _font, Out(), cts.Token));
        }
        Assert.Empty(Directory.GetFileSystemEntries(_dir));

        // 쓰는 도중 취소 + 이미 있던 파일은 그대로
        File.WriteAllText(Out(), "old");
        using (var cts = new CancellationTokenSource())
        {
            var progress = new SyncProgress(n => { if (n >= 1) cts.Cancel(); });
            Assert.ThrowsAny<OperationCanceledException>(() => SummaryPdfBuilder.Build(input, _font, Out(), cts.Token, progress));
        }
        Assert.Equal("old", File.ReadAllText(Out()));
        Assert.Single(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void Summary_MissingGlyph_ReportedNotSilent()
    {
        var input = Input("m.pdf", P(1, E(1, "a", "ok \U0001F600 end")));
        var r = SummaryPdfBuilder.Build(input, _font, Out(), CancellationToken.None);
        Assert.Contains(0x1F600, r.MissingCodepoints);
        string all = Squash(string.Concat(Texts(Out())));
        Assert.Contains("1F600", all);   // 끝에 알림 문구
        Assert.Contains("okend", all);
    }

    /// <summary>쪽을 하나씩 만들어 주는 열거. 새 쪽을 요청할 때 앞의 쪽 그림이 아직 살아 있는 수와 관리 메모리 최고치를 잰다.</summary>
    private sealed class LazyPages(int count, int w, int h)
    {
        private readonly List<WeakReference> _issued = [];
        public int Requested { get; private set; }
        public int MaxAliveWhenRequesting { get; private set; }
        public long PeakGrowth { get; private set; }

        public IEnumerable<SummaryPage> Pages()
        {
            long baseline = GC.GetTotalMemory(true);
            for (int i = 0; i < count; i++)
            {
                if (i % 10 == 0)
                {
                    // 앞서 준 쪽 그림 중 아직 안 놓인 것의 수 (요청 시점 = 빌더가 앞 쪽을 다 쓰고 다음을 달라고 할 때)
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    MaxAliveWhenRequesting = Math.Max(MaxAliveWhenRequesting, _issued.Count(r => r.IsAlive));
                    PeakGrowth = Math.Max(PeakGrowth, GC.GetTotalMemory(false) - baseline);
                }
                Requested++;
                yield return Make(i);
            }
        }

        private SummaryPage Make(int i)
        {
            var bgra = Gray(w, h);
            _issued.Add(new WeakReference(bgra));
            var e = new SummaryEntry(i + 1, "k", "a", T0, "메모 " + i, [], new PdfRectF(10, 10, 60, 40));
            return new SummaryPage(i + 1, bgra, w, h, [e]);
        }
    }

    [Fact]
    public void Summary_PagesConsumedLazily_AtMostFewAliveAtATime()
    {
        var lazy = new LazyPages(40, 200, 280);
        SummaryPdfBuilder.Build(new SummaryInput("l.pdf", T0, lazy.Pages()) { Text = Ko }, _font, Out(), CancellationToken.None);
        Assert.Equal(40, lazy.Requested);
        // 빌더가 쪽 그림을 쥐고 있지 않으면 새 쪽을 요청할 때 앞 쪽은 (한 쪽쯤 빼고) 모두 놓여 있다.
        Assert.True(lazy.MaxAliveWhenRequesting <= 2, "alive=" + lazy.MaxAliveWhenRequesting);
        Assert.True(Texts(Out()).Count > 0);
    }

    [Fact]
    public void Summary_300Pages_PeakManagedGrowthFarBelowHoldingAllBitmaps()
    {
        // 636x900 BGRA = 2.3 MB. 전부 쥐고 있으면 300쪽 = 약 690 MB. 한 쪽씩 쓰면 쪽 그림 하나 + 압축된 그림(합성 단색이라 아주 작음)뿐이다.
        // 느슨한 한계: 150 MB (시험 환경의 GC 변동을 넉넉히 둔 값).
        var lazy = new LazyPages(300, 636, 900);
        var r = SummaryPdfBuilder.Build(new SummaryInput("big.pdf", T0, lazy.Pages()) { Text = Ko }, _font, Out(), CancellationToken.None);
        Assert.Equal(300, lazy.Requested);
        Assert.Equal(300, r.EntryCount);
        Assert.True(lazy.PeakGrowth < 150L * 1024 * 1024, "peak growth MB=" + lazy.PeakGrowth / 1048576.0);
    }

    private sealed class SyncProgress(Action<int> a) : IProgress<int>
    {
        public void Report(int value) => a(value);
    }
}
