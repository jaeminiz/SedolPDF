using System.Drawing;
using Pdf.Native;
using PdfEditor.App.Tests.Panels;
using PdfEditor.App.Viewer;
using PdfEditor.Document;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Viewer;

public class RectTextSelectionTests : PanelTestBase
{
    private sealed class FakeClipboard : IClipboardService
    {
        public List<string> Texts { get; } = [];
        public Task SetTextAsync(string text) { Texts.Add(text); return Task.CompletedTask; }
    }

    private static (int, string, RectangleF) C(int i, string s, float x, float y) => (i, s, new RectangleF(x, y, 8, 12));

    // ---- 읽는 순서 (PDFium 없이) ----

    [Fact]
    public void Build_OrdersTopToBottomThenLeftToRight_RegardlessOfStreamOrder()
    {
        // 글자 번호(스트림 순서)는 뒤섞여 있다: 아랫줄이 먼저, 줄 안에서도 오른쪽이 먼저.
        var r = RectTextSelector.Build([
            C(5, "D", 20, 50), C(4, "C", 10, 50),
            C(1, "B", 10, 10), C(0, "A", 0, 10),
        ]);
        Assert.Equal("AB\r\nCD", r.Text);
    }

    [Fact]
    public void Build_SameLineWithSmallVerticalJitter_StaysOneLine()
    {
        var r = RectTextSelector.Build([C(0, "A", 0, 10), C(1, "B", 10, 11.5f), C(2, "C", 20, 9)]);
        Assert.Equal("ABC", r.Text);
    }

    [Fact]
    public void Build_RunsAreConsecutiveCharRanges()
    {
        var r = RectTextSelector.Build([C(3, "A", 0, 10), C(4, "B", 10, 10), C(5, "C", 20, 10), C(9, "D", 30, 10)]);
        Assert.Equal([(3, 3), (9, 1)], r.Runs);
    }

    [Fact]
    public void Build_Empty_ReturnsEmpty()
    {
        var r = RectTextSelector.Build([]);
        Assert.Equal("", r.Text);
        Assert.Empty(r.Runs);
    }

    [Fact]
    public void Build_TrimsTrailingSpacesOfEachLine()
    {
        var r = RectTextSelector.Build([C(0, "A", 0, 10), C(1, " ", 8, 10), C(2, "B", 0, 40), C(3, " ", 8, 40)]);
        Assert.Equal("A\r\nB", r.Text);
    }

    // ---- 모델 ----

    [Fact]
    public void Model_RectIsNormalizedAndKeptInPageCoordinates()
    {
        var m = new TextSelectionModel();
        m.BeginRect(new PageId(1), new PointF(200, 300));
        m.ExtendRect(new PointF(100, 400));
        Assert.Equal(RectangleF.FromLTRB(100, 300, 200, 400), m.RectPt);
        Assert.Null(m.Range);   // 사각형 선택에서는 글자 범위가 없다
        m.Begin(new PageId(1), 3);   // 읽는 순서 선택으로 바꾸면 사각형은 사라진다
        Assert.Null(m.RectPt);
        Assert.Equal((3, 1), m.Range);
        m.BeginRect(new PageId(1), new PointF(1, 1));
        m.Clear();
        Assert.Null(m.RectPt);
        Assert.Null(m.Page);
    }

    // ---- 실제 PDFium ----

    private async Task<(DocumentSession Session, PageId Id)> Open(string name, IReadOnlyList<string> lines, int rotate = 0, bool korean = false)
    {
        string p = Path.Combine(Dir, name);
        TestPdfFactory.CreateTextLines(p, lines, rotate, korean);
        var tab = await OpenTab(p);
        return (tab.Session!, tab.Session!.Pages[0].Id);
    }

    // 글 전체의 줄별 사각형(PDF 사용자 좌표, PdfTextPage.GetRects 그대로: Y=아래).
    private static Task<IReadOnlyList<RectangleF>> LineRects(DocumentSession s, PageId id) =>
        s.Thread.InvokeAsync(() => { var t = s.GetLoadedText(id); return t.GetRects(0, t.CharCount); });

    [Fact]
    public async Task Resolve_RectOverWholeLines_SelectsThoseLinesInOrder()
    {
        var (s, id) = await Open("a.pdf", ["ALPHA BRAVO", "CHARLIE DELTA", "ECHO FOXTROT"]);
        var lines = await LineRects(s, id);
        Assert.Equal(3, lines.Count);
        // 위쪽 두 줄(PDF 좌표에서 y가 큰 쪽)을 덮는 사각형.
        var top2 = RectangleF.Union(lines.OrderByDescending(r => r.Y).ElementAt(0), lines.OrderByDescending(r => r.Y).ElementAt(1));
        top2.Inflate(5, 2);

        var (res, rects) = await s.Thread.InvokeAsync(() => RectTextSelector.Resolve(s, id, top2));

        Assert.Equal("ALPHA BRAVO\r\nCHARLIE DELTA", res.Text);
        Assert.Equal(2, rects.Count);   // 줄별 하이라이트
    }

    [Fact]
    public async Task Resolve_RectCutsLineInMiddle_SelectsOnlyCharsWhoseCentersAreInside()
    {
        var (s, id) = await Open("b.pdf", ["ALPHA BRAVO", "CHARLIE DELTA"]);
        var top = (await LineRects(s, id)).OrderByDescending(r => r.Y).First();
        // 첫 줄의 왼쪽 절반만.
        var cut = RectangleF.FromLTRB(top.Left - 5, top.Y - 2, top.Left + top.Width / 2, top.Y + top.Height + 2);

        var (res, _) = await s.Thread.InvokeAsync(() => RectTextSelector.Resolve(s, id, cut));

        Assert.NotEmpty(res.Text);
        Assert.True(res.Text.Length < "ALPHA BRAVO".Length);
        Assert.StartsWith(res.Text, "ALPHA BRAVO");
        Assert.DoesNotContain("\r\n", res.Text);   // 아랫줄은 걸리지 않았다
    }

    [Fact]
    public async Task Resolve_RectInEmptyArea_SelectsNothing()
    {
        var (s, id) = await Open("c.pdf", ["ALPHA BRAVO"]);
        var (res, rects) = await s.Thread.InvokeAsync(() => RectTextSelector.Resolve(s, id, new RectangleF(400, 100, 100, 100)));
        Assert.Equal("", res.Text);
        Assert.Empty(rects);
    }

    [Fact]
    public async Task Resolve_Korean_SelectsKoreanLines()
    {
        var (s, id) = await Open("k.pdf", ["배관 계통도", "검토 의견서", "승인 요청"], korean: true);
        var byTop = (await LineRects(s, id)).OrderByDescending(r => r.Y).ToList();
        var middle = byTop[1];
        middle.Inflate(5, 2);

        var (res, _) = await s.Thread.InvokeAsync(() => RectTextSelector.Resolve(s, id, middle));

        Assert.Equal("검토 의견서", res.Text);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Resolve_RotatedPage_UsesPageCoordinatesConsistently(int rotate)
    {
        var (s, id) = await Open($"r{rotate}.pdf", ["ALPHA BRAVO", "CHARLIE DELTA", "ECHO FOXTROT"], rotate);
        var lines = await LineRects(s, id);
        Assert.Equal(3, lines.Count);

        // 쪽 좌표 전체를 덮으면 글 전체가 나온다.
        var (all, _) = await s.Thread.InvokeAsync(() => RectTextSelector.Resolve(s, id, new RectangleF(-1000, -1000, 3000, 3000)));
        // 이 시험 파일은 글이 쪽 회전 때문에 옆으로 누워 보이므로 읽는 순서는 따지지 않고 걸린 글자 집합만 비교한다.
        static string Letters(string s) => string.Concat(s.Where(char.IsLetter).OrderBy(c => c));
        Assert.Equal(Letters("ALPHABRAVOCHARLIEDELTAECHOFOXTROT"), Letters(all.Text));

        // 가운데 줄의 사각형(쪽 좌표)만 덮으면 그 줄만 나온다 — 쪽이 돌아가 있어도.
        var mid = lines.OrderBy(r => r.Y).ElementAt(1);
        mid.Inflate(2, 2);
        var (one, rects) = await s.Thread.InvokeAsync(() => RectTextSelector.Resolve(s, id, mid));
        Assert.Equal(Letters("CHARLIEDELTA"), Letters(one.Text));
        Assert.NotEmpty(rects);
    }

    // ---- 복사 ----

    [Fact]
    public async Task Copy_RectSelection_PutsReadingOrderTextOnClipboard()
    {
        var (s, id) = await Open("cp.pdf", ["ALPHA BRAVO", "CHARLIE DELTA", "ECHO FOXTROT"]);
        var lines = await LineRects(s, id);
        var top2 = RectangleF.Union(lines.OrderByDescending(r => r.Y).ElementAt(0), lines.OrderByDescending(r => r.Y).ElementAt(1));
        top2.Inflate(5, 2);
        var sel = new TextSelectionModel();
        sel.BeginRect(id, new PointF(top2.Left, top2.Top));
        sel.ExtendRect(new PointF(top2.Right, top2.Bottom));
        var cb = new FakeClipboard();

        var r = await TextCopier.CopyAsync(s, sel, cb);

        Assert.Equal(CopyResult.Copied, r);
        Assert.Equal("ALPHA BRAVO\r\nCHARLIE DELTA", Assert.Single(cb.Texts));
    }

    [Fact]
    public async Task Copy_RectSelection_Restricted_DoesNotTouchClipboard()
    {
        string p = Path.Combine(Dir, "rr.pdf");
        TestPdfFactory.CreateOwnerRestricted(p, allowPrint: true, allowModify: true, allowCopy: false);
        var tab = await OpenTab(p);
        var sel = new TextSelectionModel();
        sel.BeginRect(tab.Session!.Pages[0].Id, new PointF(0, 0));
        sel.ExtendRect(new PointF(600, 800));
        var cb = new FakeClipboard();

        Assert.Equal(CopyResult.Restricted, await TextCopier.CopyAsync(tab.Session, sel, cb));
        Assert.Empty(cb.Texts);
    }
}
