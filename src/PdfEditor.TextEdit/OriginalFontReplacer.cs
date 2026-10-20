using Pdf.Native;
using PdfEditor.Document;

namespace PdfEditor.TextEdit;

/// <summary>
/// 원래 글꼴로 바꾼 글자를 믿어도 되는지 확인한다(M4 결정 C4, V14 규칙). PDFium 전용 스레드에서만 부른다.
/// 편집 중인 쪽 핸들은 건드리지 않고, 그 쪽을 새로 한 번 더 열어(원본 내용) 시험용 객체로 확인한 뒤 닫는다.
/// </summary>
public static class OriginalFontReplacer
{
    public enum Result { Ok, NotText, SetTextFailed, ReadBackDiffers, GlyphNotDrawn }

    /// <summary>
    /// 규칙: ① SetText 성공 ② 되읽기(GetText)가 입력과 완전히 같음 ③ 공백 아닌 글자마다 그 글자 칸에 잉크가 있음.
    /// 서브셋에 없는 글자는 PDFium이 조용히 빼므로 ②에서 걸린다(V14: 거짓 통과 0).
    /// </summary>
    public static Result Verify(DocumentSession s, int pageIndex, int originalIndex, string newText)
    {
        s.Thread.AssertOnThread();
        using var page = s.Document.LoadPage(pageIndex);
        if (originalIndex < 0 || originalIndex >= page.ObjectCount) return Result.NotText;
        var orig = page.GetObject(originalIndex);
        if (orig.Type != PdfPageObjectType.Text) return Result.NotText;
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
        var b = probe.Bounds;
        int x0 = Math.Max(0, (int)(b.Left * scale) - 2), x1 = (int)Math.Ceiling(b.Right * scale) + 2;
        int y0 = Math.Max(0, (int)((sz.Height - b.Top) * scale) - 2), y1 = (int)Math.Ceiling((sz.Height - b.Bottom) * scale) + 2;
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
                int cx0 = (int)(r.Left * scale) - x0, cx1 = (int)Math.Ceiling(r.Right * scale) - x0;
                int cy0 = (int)((sz.Height - hi) * scale) - y0, cy1 = (int)Math.Ceiling((sz.Height - lo) * scale) - y0;
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
