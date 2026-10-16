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
        if (d.Opacity < 1f) sb.Append("/GS gs\n");
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
