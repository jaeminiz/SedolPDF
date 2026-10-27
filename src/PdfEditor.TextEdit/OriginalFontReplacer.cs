using Pdf.Native;
using PdfEditor.Document;

namespace PdfEditor.TextEdit;

/// <summary>
/// 원래 글꼴로 바꾼 글자를 믿어도 되는지 확인한다(M4 결정 C4, V14 규칙). PDFium 전용 스레드에서만 부른다.
/// 편집 중인 쪽 핸들은 건드리지 않고, 그 쪽을 새로 한 번 더 열어(원본 내용) 시험용 객체로 확인한 뒤 닫는다.
/// </summary>
public static class OriginalFontReplacer
{
    public enum Result { Ok, NotText, SetTextFailed, ReadBackDiffers, GlyphNotDrawn, GlyphNotInSubset }

    /// <summary>
    /// 규칙: ① SetText 성공 ② 되읽기(GetText)가 입력과 완전히 같음 ③ 공백 아닌 글자마다 그 글자 칸에 잉크가 있음.
    /// 서브셋에 없는 글자는 PDFium이 조용히 빼므로 ②에서 걸린다(V14: 거짓 통과 0).
    /// </summary>
    public static Result Verify(DocumentSession s, int pageIndex, int originalIndex, string newText) => Verify(s, pageIndex, originalIndex, newText, null);

    /// <summary>
    /// 문단 편집용(M5): ④의 "원래 줄에 있던 글자"를 그 글 객체 하나가 아니라 <paramref name="allowedChars"/>(같은 글꼴로 보인 문단의 모든 글자)로 넓힌다.
    /// 같은 글꼴 사전의 서브셋은 문단 전체의 글자를 가지므로 안전하다고 보지만, 같은 이름의 다른 사전이 섞이면 어긋날 수 있다(검증 필요) — ②③이 마지막 보호.
    /// </summary>
    public static Result Verify(DocumentSession s, int pageIndex, int originalIndex, string newText, IReadOnlySet<char>? allowedChars)
    {
        s.Thread.AssertOnThread();
        using var page = s.Document.LoadPage(pageIndex);
        if (originalIndex < 0 || originalIndex >= page.ObjectCount) return Result.NotText;
        var orig = page.GetObject(originalIndex);
        if (orig.Type != PdfPageObjectType.Text) return Result.NotText;
        // ④ 임베드된(서브셋일 수 있는) 글꼴은 그 줄에 이미 있던 글자만 쓴다. 서브셋에 없는 글자는 빈 글리프(.notdef 네모)로 그려지는데,
        //    ToUnicode가 있는 글꼴에서는 되읽기·잉크 검사를 통과해 버릴 수 있다(2026-10-20 사용자 보고: 글자가 네모로 나옴).
        if (orig.Font is null or { Embedded: true })
        {
            if (allowedChars is null)
            {
                string? before;
                using (var t0 = page.LoadText()) before = orig.GetText(t0);
                if (before is null) return Result.GlyphNotInSubset;
                foreach (char c in newText)
                    if (!char.IsWhiteSpace(c) && !before.Contains(c)) return Result.GlyphNotInSubset;
            }
            else
            {
                foreach (char c in newText)
                    if (!char.IsWhiteSpace(c) && !allowedChars.Contains(c)) return Result.GlyphNotInSubset;
            }
        }
        var probe = page.CreateTextLike(orig, newText);
        if (probe is null) return Result.SetTextFailed;

        // 시험 객체만 남긴다(이 핸들은 버린다 — GenerateContent 없이 닫으면 원본 그대로, V12).
        // 렌더 모드는 원래 것을 쓴다: 보이지 않는 글자(모드 3 등)는 잉크 검사에서 걸려 앱 글꼴로 간다.
        for (int i = 0; i < page.ObjectCount; i++) page.GetObject(i).IsActive = false;
        probe.FillColor = new PdfRgba(0, 0, 0, 255);
        page.InsertObject(probe);

        using var tp = page.LoadText();
        if (probe.GetText(tp) != newText) return Result.ReadBackDiffers;
        return AllGlyphsInked(page, probe, tp, newText) ? Result.Ok : Result.GlyphNotDrawn;
    }

    private static bool AllGlyphsInked(PdfPage page, PdfPageObject probe, PdfTextPage tp, string text)
    {
        const double scale = 4;
        var sz = page.SizePt;
        // 쪽 회전(0.4.0): 객체·글자 사각형은 회전 전 쪽 좌표, 타일은 회전된 화면 좌표다. 쪽 좌표 → 화면 좌표(위가 y=0)로 옮긴다.
        int rot = page.Rotation & 3;
        bool odd = rot % 2 != 0;
        double w0 = odd ? sz.Height : sz.Width, h0 = odd ? sz.Width : sz.Height;
        (double X, double Y) Dev(double x, double y) => rot switch { 1 => (y, x), 2 => (w0 - x, y), 3 => (h0 - y, w0 - x), _ => (x, h0 - y) };
        (double L, double T, double R, double B) DevBox(double l, double b, double r, double t)
        {
            var c = new[] { Dev(l, b), Dev(l, t), Dev(r, b), Dev(r, t) };
            return (c.Min(p => p.X), c.Min(p => p.Y), c.Max(p => p.X), c.Max(p => p.Y));
        }
        var b0 = probe.Bounds;
        var pb = DevBox(b0.Left, b0.Bottom, b0.Right, b0.Top);
        int x0 = Math.Max(0, (int)(pb.L * scale) - 2), x1 = (int)Math.Ceiling(pb.R * scale) + 2;
        int y0 = Math.Max(0, (int)(pb.T * scale) - 2), y1 = (int)Math.Ceiling(pb.B * scale) + 2;
        int bw = Math.Max(1, x1 - x0), bh = Math.Max(1, y1 - y0);
        if ((long)bw * bh > 40_000_000) return false;   // 비정상적으로 큰 객체: 확인하지 못하면 원래 글꼴을 쓰지 않는다
        var bmp = page.RenderTile(scale, x0, y0, bw, bh, withAnnotations: false);

        int expected = text.Count(c => !char.IsWhiteSpace(c)), seen = 0, inked = 0;
        for (int c = 0; c < tp.CharCount; c++)
        {
            if (string.IsNullOrWhiteSpace(tp.GetText(c, 1))) continue;
            seen++;
            foreach (var r in tp.GetRects(c, 1))
            {
                float lo = Math.Min(r.Top, r.Bottom), hi = Math.Max(r.Top, r.Bottom);
                var d = DevBox(r.Left, lo, r.Right, hi);
                int cx0 = (int)(d.L * scale) - x0, cx1 = (int)Math.Ceiling(d.R * scale) - x0;
                int cy0 = (int)(d.T * scale) - y0, cy1 = (int)Math.Ceiling(d.B * scale) - y0;
                if (HasInk(bmp, bw, bh, cx0, cy0, cx1, cy1)) { inked++; break; }
            }
        }
        return seen == expected && inked == seen;
    }

    private static bool HasInk(byte[] bmp, int bw, int bh, int x0, int y0, int x1, int y1)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(bh, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(bw, x1); x++)
            {
                int o = (y * bw + x) * 4;
                if (bmp[o] < 128 && bmp[o + 1] < 128 && bmp[o + 2] < 128) return true;
            }
        return false;
    }
}
