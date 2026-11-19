using System.Globalization;
using System.Text;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Appearance;

/// <summary>AppearanceDrawing을 PDF 내용 스트림 문자열로 바꾼다(InvariantCulture).</summary>
public static class PdfContentWriter
{
    public static string Write(AppearanceDrawing d)
    {
        var sb = new StringBuilder();
        sb.Append("q\n");
        if (d.Transform is { } tm)
            sb.Append(Num(tm.A)).Append(' ').Append(Num(tm.B)).Append(' ').Append(Num(tm.C)).Append(' ').Append(Num(tm.D)).Append(' ')
              .Append(Num(tm.E)).Append(' ').Append(Num(tm.F)).Append(" cm\n");
        if (d.Opacity < 1f || d.Multiply) sb.Append("/GS gs\n");
        bool dashOn = false;
        foreach (var op in d.Ops)
        {
            if (op.Fill is null && op.Stroke is null) continue;
            if (op.Fill is { } f) sb.Append(Color(f)).Append(" rg\n");
            if (op.Stroke is { } s)
            {
                sb.Append(Color(s)).Append(" RG\n");
                sb.Append(Num(op.StrokeWidth)).Append(" w\n");
                sb.Append((int)op.Cap).Append(" J\n");
                sb.Append((int)op.Join).Append(" j\n");
                // 대시는 같은 q ... Q 안에서 이어지므로 실선 Op가 앞의 대시를 물려받지 않게 되돌린다(실선만 있는 그림의 출력은 그대로).
                bool dashed = !op.Dash.IsDefaultOrEmpty;
                if (dashed) sb.Append('[').Append(string.Join(' ', op.Dash.Select(Num))).Append("] 0 d\n");
                else if (dashOn) sb.Append("[] 0 d\n");
                dashOn = dashed;
            }
            foreach (var seg in op.Path)
            {
                switch (seg)
                {
                    case MoveTo m: sb.Append(Pt(m.P)).Append(" m\n"); break;
                    case LineTo l: sb.Append(Pt(l.P)).Append(" l\n"); break;
                    case CubicTo c: sb.Append(Pt(c.C1)).Append(' ').Append(Pt(c.C2)).Append(' ').Append(Pt(c.P)).Append(" c\n"); break;
                    case ClosePath: sb.Append("h\n"); break;
                }
            }
            sb.Append(op.Fill is not null && op.Stroke is not null ? "B" : op.Fill is not null ? "f" : "S").Append('\n');
        }
        foreach (var im in d.Images)
        {
            sb.Append("q\n").Append(Num(im.Rect.Width)).Append(" 0 0 ").Append(Num(im.Rect.Height)).Append(' ')
              .Append(Num(im.Rect.Left)).Append(' ').Append(Num(im.Rect.Bottom)).Append(" cm\n")
              .Append('/').Append(im.ImageResource).Append(" Do\nQ\n");
        }
        foreach (var t in d.Texts)
        {
            if (t.Glyphs.IsEmpty) continue;
            sb.Append("BT\n/").Append(t.FontResource).Append(' ').Append(Num(t.SizePt)).Append(" Tf\n")
              .Append(Color(t.Color)).Append(" rg\n");
            // 합성 굵게(윤곽선 번짐): 채움+선(2 Tr), 선 색은 글자 색, 굵기는 글자 크기 비례. q ... Q 안이라 다른 묶음에 새지 않지만 끝에 되돌린다.
            if (t.Bold)
                sb.Append(Color(t.Color)).Append(" RG\n").Append(Num(t.SizePt * AppFontCatalog.SyntheticBoldStrokeRatio)).Append(" w\n1 j\n2 Tr\n");
            string skew = t.Italic ? Num(AppFontCatalog.SyntheticItalicSkew) : "0";   // 합성 기울임: 글자 원점 기준 전단
            // 같은 기준선의 글자는 Tm 한 번 + TJ 하나로 쓴다(자리는 TJ 조정값으로 맞춤). 글자마다 Tm을 쓰면 PDF를 다시 열 때
            // 글자 하나하나가 따로 잡히고 글자 추출도 쪼개진다(0.14.0 손 시험: 고친 날짜가 숫자 낱개로 잡힘).
            var gl = t.Glyphs;
            for (int i = 0; i < gl.Length;)
            {
                int j = i + 1;
                if (t.SizePt > 0)
                    while (j < gl.Length && Math.Abs(gl[j].Origin.Y - gl[i].Origin.Y) < 0.001f && gl[j].Origin.X >= gl[j - 1].Origin.X) j++;
                sb.Append("1 0 ").Append(skew).Append(" 1 ").Append(Pt(gl[i].Origin)).Append(" Tm\n");
                if (j - i == 1) { sb.Append('<').Append(gl[i].Cid.ToString("X4")).Append("> Tj\n"); i = j; continue; }
                sb.Append('[');
                for (int k = i; k < j; k++)
                {
                    sb.Append('<').Append(gl[k].Cid.ToString("X4")).Append('>');
                    if (k + 1 < j)
                    {
                        // /W에 쓰인 폭(AppearanceDrawing: 폭×1000/크기 반올림)과 실제 다음 글자까지 거리의 차이만큼 TJ로 보정
                        double natural = Math.Round(gl[k].Advance * 1000f / t.SizePt);
                        double adj = natural - (gl[k + 1].Origin.X - gl[k].Origin.X) * 1000.0 / t.SizePt;
                        if (Math.Abs(adj) >= 0.005) sb.Append(adj.ToString("0.##", CultureInfo.InvariantCulture));
                    }
                }
                sb.Append("] TJ\n");
                i = j;
            }
            if (t.Bold) sb.Append("0 Tr\n");
            sb.Append("ET\n");
        }
        sb.Append("Q\n");
        return sb.ToString();
    }

    private static string Pt(PdfPointF p) => Num(p.X) + " " + Num(p.Y);

    private static string Color(PdfColor c) => Num(c.R / 255f) + " " + Num(c.G / 255f) + " " + Num(c.B / 255f);

    private static string Num(float v)
    {
        var s = v.ToString("0.###", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}
