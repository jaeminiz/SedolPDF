using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfEditor.Appearance.Fonts;

/// <summary>앱이 가진 대체 글꼴 종류. 고딕(Noto Sans KR) / 명조(Noto Serif KR). 둘 다 OFL.</summary>
public enum AppFontFamily { Sans, Serif }

/// <summary>원래 글꼴에서 읽은 모양 단서(글꼴 사전에서 읽거나 PDFium 이름만 있을 때 이름으로).</summary>
public sealed record PdfFontInfo(string? BaseName, int Flags = 0, int FontWeight = 0, double ItalicAngle = 0, bool ForceBold = false)
{
    public const int FlagSerif = 1 << 1, FlagItalic = 1 << 6, FlagForceBold = 1 << 18;

    /// <summary>글꼴 사전(Type0이면 첫 DescendantFonts)에서 읽는다. 읽을 수 없는 항목은 비워 둔다(이름 규칙으로 보충).</summary>
    public static PdfFontInfo FromFontDictionary(PdfDictionary font)
    {
        string? name = font.Elements.GetName("/BaseFont");
        var d = font;
        if (font.Elements.GetName("/Subtype") == "/Type0"
            && font.Elements["/DescendantFonts"] is { } df
            && Resolve(df) is PdfArray arr && arr.Elements.Count > 0 && Resolve(arr.Elements[0]) is PdfDictionary desc)
            d = desc;
        int flags = 0, weight = 0;
        double angle = 0;
        if (d.Elements["/FontDescriptor"] is { } fd && Resolve(fd) is PdfDictionary dd)
        {
            flags = dd.Elements.GetInteger("/Flags");
            weight = dd.Elements.GetInteger("/FontWeight");
            angle = dd.Elements.GetReal("/ItalicAngle");
            name ??= dd.Elements.GetName("/FontName");
        }
        return new PdfFontInfo(string.IsNullOrEmpty(name) ? null : name.TrimStart('/'), flags, weight, angle, (flags & FlagForceBold) != 0);
    }

    private static PdfItem? Resolve(PdfItem item) => item is PdfReference r ? r.Value : item;
}

/// <summary>고른 대체 글꼴. Font는 불러온 글꼴(명조가 없으면 고딕으로 물러나고 <see cref="FellBack"/>이 참).</summary>
public sealed record AppFontChoice(AppFontFamily Family, AppFont Font, bool SyntheticBold, bool SyntheticItalic, bool FellBack = false);

/// <summary>
/// 대체 글꼴 선택(M5 C11, 사용자 결정 U2). 원본이 명조/세리프 계열이면 명조, 아니면 고딕. 굵기·기울임은 가짜(렌더 모드 2 + 가는 선 / 기울임 행렬).
/// 번들 글꼴은 OFL 글꼴만 둔다. 시스템 글꼴 직접 선택은 범위 밖이다.
/// </summary>
public sealed class AppFontCatalog
{
    public const string SerifRelativePath = "assets/fonts/NotoSerifKR-Regular.otf";
    /// <summary>가짜 굵게의 선 굵기 = 글자 크기 × 이 값(렌더 모드 2의 stroke 폭).</summary>
    public const float SyntheticBoldStrokeRatio = 0.03f;
    /// <summary>가짜 기울임 기울기(탄젠트). 약 12°.</summary>
    public const float SyntheticItalicSkew = 0.21f;

    private static readonly string[] SerifNames =
        ["serif", "times", "batang", "myeongjo", "mincho", "garamond", "georgia", "palatino", "bookman", "century", "gungsuh", "gungseo", "바탕", "명조", "궁서", "함초롬바탕", "휴먼명조"];
    private static readonly string[] SansOverrides = ["sans", "gothic", "dotum", "gulim", "고딕", "돋움", "굴림", "arial", "helvetica"];
    private static readonly string[] BoldNames = ["bold", "black", "heavy", "semibold", "demibold", "굵", "-bd", ",bd"];
    private static readonly string[] ItalicNames = ["italic", "oblique", "기울"];

    private readonly AppFont _sans;
    private readonly Lazy<AppFont?> _serif;

    public AppFontCatalog(AppFont sans, Func<AppFont?>? serifProvider = null)
    {
        _sans = sans;
        _serif = new Lazy<AppFont?>(serifProvider ?? (() => null));
    }

    /// <summary>앱 폴더에서 명조를 처음 필요할 때 읽는다(파일이 없으면 고딕으로 물러난다).</summary>
    public static AppFontCatalog ForBaseDirectory(AppFont sans, string baseDirectory) =>
        new(sans, () =>
        {
            var data = AppFontFile.ReadSerifOrEmpty(baseDirectory);
            return data.Length > 0 ? AppFont.FromBytes(data) : null;
        });

    public AppFont Sans => _sans;
    /// <summary>명조 글꼴. 파일이 없으면 null.</summary>
    public AppFont? Serif => _serif.Value;

    /// <summary>글꼴 가족과 가짜 굵게·기울임을 정한다(글꼴 파일을 읽지 않는다).</summary>
    public static (AppFontFamily Family, bool Bold, bool Italic) Classify(PdfFontInfo? original)
    {
        if (original is null) return (AppFontFamily.Sans, false, false);
        string name = StripSubsetTag(original.BaseName ?? "").ToLowerInvariant();
        bool nameSans = SansOverrides.Any(name.Contains);
        bool serif = (original.Flags & PdfFontInfo.FlagSerif) != 0 || (!nameSans && SerifNames.Any(name.Contains));
        // 세리프 플래그가 있으면 이름과 상관없이 명조. 이름 규칙은 플래그가 없을 때만, 고딕 계열 이름(Gothic 등)이면 쓰지 않는다.
        bool bold = original.ForceBold || original.FontWeight >= 600 || BoldNames.Any(name.Contains);
        bool italic = (original.Flags & PdfFontInfo.FlagItalic) != 0 || Math.Abs(original.ItalicAngle) > 0.5 || ItalicNames.Any(name.Contains);
        return (serif ? AppFontFamily.Serif : AppFontFamily.Sans, bold, italic);
    }

    public AppFontChoice Choose(PdfFontInfo? original)
    {
        var (family, bold, italic) = Classify(original);
        if (family == AppFontFamily.Serif && Serif is { } serif)
            return new AppFontChoice(AppFontFamily.Serif, serif, bold, italic);
        return new AppFontChoice(AppFontFamily.Sans, _sans, bold, italic, FellBack: family == AppFontFamily.Serif);
    }

    private static string StripSubsetTag(string n) => n.Length > 7 && n[6] == '+' && n.Take(6).All(char.IsAsciiLetterUpper) ? n[7..] : n;

    /// <summary>
    /// OS/2 fsType 확인(OpenType): PDF에 서브셋을 넣어도 되는가. 0(제한 없음) 또는 0x0008(편집 가능 포함)만 허용하고,
    /// 0x0002(포함 금지)·0x0004(미리보기/인쇄만)·0x0100(서브셋 금지)·0x0200(비트맵만)이면 거부한다. OS/2 표가 없거나 읽을 수 없으면 거부.
    /// </summary>
    public static bool FsTypeAllowsEmbedding(byte[] fontFile)
    {
        int? fs = ReadFsType(fontFile);
        if (fs is not int v) return false;
        if ((v & 0x0100) != 0 || (v & 0x0200) != 0) return false;
        int usage = v & 0x000E;
        return usage == 0 || (usage & 0x0008) != 0 && (usage & 0x0002) == 0;
    }

    /// <summary>OS/2 fsType 값. 읽을 수 없으면 null.</summary>
    public static int? ReadFsType(byte[] d)
    {
        if (d is null || d.Length < 12) return null;
        if (d[0] == 't' && d[1] == 't' && d[2] == 'c' && d[3] == 'f') return null;   // 글꼴 모음은 지원하지 않음
        int n = (d[4] << 8) | d[5];
        for (int i = 0; i < n; i++)
        {
            int rec = 12 + 16 * i;
            if (rec + 16 > d.Length) return null;
            if (d[rec] == 'O' && d[rec + 1] == 'S' && d[rec + 2] == '/' && d[rec + 3] == '2')
            {
                long off = ((long)d[rec + 8] << 24) | ((long)d[rec + 9] << 16) | ((long)d[rec + 10] << 8) | d[rec + 11];
                if (off + 10 > d.Length) return null;
                return (d[off + 8] << 8) | d[off + 9];
            }
        }
        return null;
    }
}
