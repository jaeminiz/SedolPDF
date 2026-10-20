using System.Globalization;
using PdfEditor.Document.Annotations;

namespace PdfEditor.Document.Annotations.Reading;

/// <summary>FreeText의 /DA 문자열("0 0 1 rg 1 0 0 RG /Helv 14 Tf")에서 글자 크기·글자 색(rg·g·k)·테두리 색(RG·G·K)을 뽑는다. 알 수 없는 연산자는 건너뛴다.</summary>
internal static class DefaultAppearance
{
    public static (float? Size, PdfColor? Color, PdfColor? Stroke) Parse(string? da)
    {
        if (string.IsNullOrWhiteSpace(da)) return (null, null, null);
        float? size = null;
        PdfColor? color = null, stroke = null;
        var stack = new List<double>();
        foreach (var tok in da.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) { stack.Add(v); continue; }
            switch (tok)
            {
                case "Tf":
                    if (stack.Count >= 1) size = (float)stack[^1];   // 앞의 글꼴 이름(/Helv)은 숫자가 아니라 스택에 없다
                    break;
                case "G" when stack.Count >= 1:
                    stroke = Rgb(stack[^1], stack[^1], stack[^1]);
                    break;
                case "RG" when stack.Count >= 3:
                    stroke = Rgb(stack[^3], stack[^2], stack[^1]);
                    break;
                case "K" when stack.Count >= 4:
                    stroke = Cmyk(stack);
                    break;
                case "g" when stack.Count >= 1:
                    color = Rgb(stack[^1], stack[^1], stack[^1]);
                    break;
                case "rg" when stack.Count >= 3:
                    color = Rgb(stack[^3], stack[^2], stack[^1]);
                    break;
                case "k" when stack.Count >= 4:
                    color = Cmyk(stack);
                    break;
            }
            stack.Clear();
        }
        return (size, color, stroke);
    }

    private static PdfColor Cmyk(List<double> s)
    {
        double k = 1 - Clamp(s[^1]);
        return Rgb((1 - Clamp(s[^4])) * k, (1 - Clamp(s[^3])) * k, (1 - Clamp(s[^2])) * k);
    }

    private static double Clamp(double v) => Math.Clamp(v, 0, 1);

    private static PdfColor Rgb(double r, double g, double b)
        => new(B(r), B(g), B(b));

    private static byte B(double v) => (byte)Math.Round(Clamp(v) * 255, MidpointRounding.AwayFromZero);
}
