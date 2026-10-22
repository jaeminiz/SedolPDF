using System.Drawing;
using System.Text;
using Pdf.Native;
using PdfEditor.Document;

namespace PdfEditor.App.Viewer;

/// <summary>사각형 선택의 결과: 읽는 순서(위→아래, 왼→오)의 글, 하이라이트할 글자 번호 묶음(연속 구간).</summary>
public sealed record RectTextResult(string Text, IReadOnlyList<(int Start, int Count)> Runs)
{
    public static readonly RectTextResult Empty = new("", []);
}

/// <summary>
/// 사각형 안에 걸린 글자만 고르는 계산. 글자 판정은 PDFium 글자 상자의 중심점이 사각형 안인지로 한다(PdfTextPage.GetCharsInRect).
/// 읽는 순서는 화면에 보이는 모양(회전 반영) 기준이다.
/// </summary>
public static class RectTextSelector
{
    /// <summary>글자 상자는 화면 좌표(위가 작은 값)로 준다. 줄은 첫 글자의 세로 범위에 중심이 드는 글자끼리 묶는다.</summary>
    public static RectTextResult Build(IReadOnlyList<(int Index, string Text, RectangleF ViewBox)> chars)
    {
        if (chars.Count == 0) return RectTextResult.Empty;

        // 하이라이트용 연속 구간(PDFium 번호 기준).
        var runs = new List<(int Start, int Count)>();
        var ordered = chars.OrderBy(c => c.Index).ToList();
        int start = ordered[0].Index, prev = start;
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Index == prev + 1) { prev = ordered[i].Index; continue; }
            runs.Add((start, prev - start + 1));
            start = prev = ordered[i].Index;
        }
        runs.Add((start, prev - start + 1));

        // 공백은 상자가 믿을 수 없다(높이 0이거나 줄 밖에 놓이는 글꼴이 있다). 줄 묶기에서는 빼고,
        // 바로 앞에 선택된 보이는 글자 뒤에 붙인다(줄 끝에 남은 공백은 아래에서 잘린다).
        var visible = ordered.Where(c => !IsBlank(c.Text)).ToList();
        if (visible.Count == 0) return new RectTextResult("", runs);
        var withSpace = new Dictionary<int, string>();
        foreach (var c in ordered.Where(c => IsBlank(c.Text)))
        {
            var before = visible.LastOrDefault(v => v.Index < c.Index);
            if (before.Text is not null) withSpace[before.Index] = (withSpace.TryGetValue(before.Index, out var s) ? s : before.Text) + " ";
        }
        chars = [.. visible.Select(c => withSpace.TryGetValue(c.Index, out var t) ? (c.Index, t, c.ViewBox) : c)];

        // 읽는 순서: 세로 중심으로 줄을 묶고, 줄 안에서는 왼쪽부터.
        var byY = chars.OrderBy(c => c.ViewBox.Top + c.ViewBox.Height / 2).ToList();
        var lines = new List<(float Top, float Bottom, List<(int Index, string Text, RectangleF ViewBox)> Items)>();
        foreach (var c in byY)
        {
            float cy = c.ViewBox.Top + c.ViewBox.Height / 2;
            if (lines.Count > 0 && cy >= lines[^1].Top && cy <= lines[^1].Bottom) { lines[^1].Items.Add(c); continue; }
            lines.Add((c.ViewBox.Top, c.ViewBox.Bottom, [c]));
        }
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) sb.Append("\r\n");
            var items = lines[i].Items.OrderBy(c => c.ViewBox.Left + c.ViewBox.Width / 2).ThenBy(c => c.Index);
            var line = new StringBuilder();
            foreach (var c in items) line.Append(c.Text);
            sb.Append(line.ToString().TrimEnd());
        }
        return new RectTextResult(sb.ToString(), runs);
    }

    private static bool IsBlank(string s) => string.IsNullOrWhiteSpace(s);

    /// <summary>전용 스레드 안에서 부른다. 사각형은 쪽 좌표(pt). 쪽이 문서에 없으면 빈 결과.</summary>
    public static (RectTextResult Result, IReadOnlyList<RectangleF> Rects) Resolve(DocumentSession session, PageId id, RectangleF rectPt)
    {
        if (session.IndexOf(id) < 0) return (RectTextResult.Empty, []);
        // 쪽 핸들은 경계·회전을 읽는 데만 쓰고 놓는다(다음 GetLoaded*가 닫을 수 있다).
        var page = session.GetLoadedPage(id);
        var tr = new PageTransform(page.BoundsPt, page.Rotation, 1.0);
        var text = session.GetLoadedText(id);
        var chars = text.GetCharsInRect(rectPt);
        var view = chars.Select(c =>
        {
            var v = tr.ToView(c.Box);
            return (c.Index, c.Text, new RectangleF((float)v.X, (float)v.Y, (float)v.W, (float)v.H));
        }).ToList();
        var result = Build(view);
        var rects = new List<RectangleF>();
        foreach (var (s, n) in result.Runs) rects.AddRange(text.GetRects(s, n));
        return (result, rects);
    }
}
