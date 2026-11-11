using System.Globalization;
using System.Reflection;
using System.Security;
using System.Text;

namespace PdfEditor.Document.Annotations;

/// <summary>
/// FreeText(텍스트 상자)의 /DA·/DS·/RC를 Acrobat·PDF-XChange 관례로 만든다(0.10.0, 설계서 4.2).
/// 관례: /DS가 있으면 /DA의 rg가 테두리 색이고 글자 색·글꼴 크기는 /DS와 서식 있는 /RC가 정한다.
/// 근거: Acrobat XI 11.0.23에서 직접 확인(2026-11-11) — 예전 키(rg=글자색)는 테두리를 검게 하고, 새 키는 빨간 테두리·20pt를 지킨다.
/// </summary>
public static class FreeTextRichText
{
    private const string SansFamily = "'Malgun Gothic',sans-serif";
    private const string SerifFamily = "'Adobe Myungjo Std','Batang',serif";

    /// <summary>/DA: 테두리가 있으면 `{테두리} rg {테두리} RG /Helv {크기} Tf`, 없으면 `{글자} rg /Helv {크기} Tf`.</summary>
    public static string Da(Annotation a)
    {
        var tc = a.TextColor ?? new PdfColor(0, 0, 0);
        string size = N(a.FontSizePt);
        if (a.Color is { } bc && a.BorderWidth > 0)
        {
            string c = $"{N(bc.R / 255f)} {N(bc.G / 255f)} {N(bc.B / 255f)}";
            return $"{c} rg {c} RG /Helv {size} Tf";
        }
        return $"{N(tc.R / 255f)} {N(tc.G / 255f)} {N(tc.B / 255f)} rg /Helv {size} Tf";
    }

    /// <summary>/DS: CSS 선언. 서식(굵게·기울임·밑줄·취소선)은 있을 때만 붙인다.</summary>
    public static string Ds(Annotation a)
    {
        var tc = a.TextColor ?? new PdfColor(0, 0, 0);
        var sb = new StringBuilder("font-family:");
        sb.Append(a.TextFamily == TextFontFamily.Serif ? SerifFamily : SansFamily);
        sb.Append(";font-size:").Append(a.FontSizePt.ToString("0.#", CultureInfo.InvariantCulture)).Append("pt");
        sb.Append(";color:").Append(Hex(tc));
        sb.Append(";text-align:").Append(a.Align switch { TextAlign.Center => "center", TextAlign.Right => "right", _ => "left" });
        if (a.Bold) sb.Append(";font-weight:bold");
        if (a.Italic) sb.Append(";font-style:italic");
        if (a.Underline && a.StrikeThrough) sb.Append(";text-decoration:underline line-through");
        else if (a.Underline) sb.Append(";text-decoration:underline");
        else if (a.StrikeThrough) sb.Append(";text-decoration:line-through");
        return sb.ToString();
    }

    /// <summary>/RC: 서식 있는 글. 줄마다 문단 하나, 빈 줄은 `&lt;br/&gt;`. 다른 프로그램을 사칭하지 않고 APIVersion에 우리 이름과 버전을 쓴다.</summary>
    public static string Rc(Annotation a, string version)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\"?><body xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\" xfa:APIVersion=\"SedolPDF:");
        sb.Append(SecurityElement.Escape(version)).Append("\" xfa:spec=\"2.0.2\" style=\"").Append(Ds(a).Replace("\"", "&quot;")).Append("\">");
        foreach (var line in (a.Contents ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            sb.Append("<p dir=\"ltr\">");
            sb.Append(line.Length == 0 ? "<br/>" : SecurityElement.Escape(line));
            sb.Append("</p>");
        }
        return sb.Append("</body>").ToString();
    }

    /// <summary>/RC에 쓰는 우리 버전(어셈블리 정보 버전에서 `+해시`를 뗀 것).</summary>
    public static string AppVersion { get; } = Version();

    private static string Version()
    {
        string? v = typeof(FreeTextRichText).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(v)) return "0";
        int plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    internal static string Hex(PdfColor c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string N(float v)
    {
        var s = v.ToString("0.###", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}
