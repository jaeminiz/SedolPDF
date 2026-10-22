using System.Text;

namespace PdfEditor.ContentStream.Fonts;

/// <summary>
/// 글 줄 하나를 쓸 때의 글 상태. FillOps·StrokeOps는 색 연산자 그대로(예: "0 0 0 rg", "0.2 g")이고 없으면 쓰지 않는다.
/// Tz는 퍼센트(100 = 보통). RenderMode 2(채움+선)이면 StrokeWidth만큼 가는 선으로 가짜 굵게(C11).
/// </summary>
public readonly record struct TextLineStyle(float SizePt, float Tc = 0, float Tz = 100, float Ts = 0, int RenderMode = 0,
    string? FillOps = null, string? StrokeOps = null, float StrokeWidth = 0);

/// <summary>코드북 글자열 → <c>q … BT … Tj/TJ ET Q</c> 바이트(M5 C8·C15). 글 상태를 q…Q 안에 가둬 앞뒤 객체에 번지지 않는다.</summary>
public static class TextLineWriter
{
    /// <param name="placement">기준선 왼쪽 점을 원점으로 한 로컬 좌표 → 쪽(또는 내용 스트림 현재 좌표계) 변환. 글자 크기는 포함하지 않는다(Tf로 씀).</param>
    /// <param name="justifyExtraAtSpaces">있으면 공백 글자 <b>뒤(다음 글이 이어질 때만)</b>마다 이만큼(글 공간 단위, Tz 적용 뒤의 실제 이동량)을 더 벌린다 — <c>TJ</c> 숫자로.
    /// Tw는 2바이트 글꼴에서 안 먹으므로 쓰지 않는다(C15).</param>
    public static byte[] Write(FontCodebook book, string fontResName, EncodedText line, TextLineStyle style, Mat placement, float? justifyExtraAtSpaces = null)
    {
        var sb = new StringBuilder();
        sb.Append("q\n");
        if (!string.IsNullOrEmpty(style.FillOps)) sb.Append(style.FillOps).Append('\n');
        if (!string.IsNullOrEmpty(style.StrokeOps)) sb.Append(style.StrokeOps).Append('\n');
        if (style.RenderMode is 1 or 2 or 5 or 6 && style.StrokeWidth > 0) sb.Append(Mat.Fmt(style.StrokeWidth)).Append(" w\n");
        sb.Append(Mat.Fmt(style.Tc)).Append(" Tc ").Append(Mat.Fmt(style.Tz)).Append(" Tz ")
          .Append(Mat.Fmt(style.Ts)).Append(" Ts ").Append(style.RenderMode).Append(" Tr\n");
        sb.Append("BT\n/").Append(NameToken(fontResName)).Append(' ').Append(Mat.Fmt(style.SizePt)).Append(" Tf\n");
        sb.Append(placement.ToOperands()).Append(" Tm\n");

        int unit = book.TwoByte ? 2 : 1;
        if (justifyExtraAtSpaces is { } extra && Math.Abs(extra) > 1e-6f && line.Text.Contains(' '))
        {
            double tz = style.Tz / 100.0;
            double adj = -extra * 1000.0 / (Math.Max(style.SizePt, 1e-6) * Math.Max(tz, 1e-6));
            sb.Append('[');
            int start = 0;
            for (int i = 0; i < line.Text.Length; i++)
            {
                if (line.Text[i] != ' ' || i == line.Text.Length - 1) continue;
                AppendHex(sb, line.Bytes, start * unit, (i + 1 - start) * unit);
                sb.Append(' ').Append(Mat.Fmt(adj)).Append(' ');
                start = i + 1;
            }
            AppendHex(sb, line.Bytes, start * unit, line.Bytes.Length - start * unit);
            sb.Append("] TJ");
        }
        else
        {
            AppendHex(sb, line.Bytes, 0, line.Bytes.Length);
            sb.Append(" Tj");
        }
        sb.Append("\nET\nQ\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static void AppendHex(StringBuilder sb, byte[] bytes, int offset, int count)
    {
        sb.Append('<').Append(Convert.ToHexString(bytes, offset, count)).Append('>');
    }

    private static string NameToken(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (c is > ' ' and < '\u007f' && "()<>[]{}/%#".IndexOf(c) < 0) sb.Append(c);
            else sb.Append('#').Append(((int)c & 0xFF).ToString("X2"));
        }
        return sb.ToString();
    }
}
