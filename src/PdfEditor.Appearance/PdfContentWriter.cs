using System.Globalization;
using System.Text;
using Pdf.Native;
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
        if (d.Opacity < 1f) sb.Append("/GS gs\n");
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
            foreach (var g in t.Glyphs)
                sb.Append("1 0 0 1 ").Append(Pt(g.Origin)).Append(" Tm\n<").Append(g.Cid.ToString("X4")).Append("> Tj\n");
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
