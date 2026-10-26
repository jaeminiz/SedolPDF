using PdfEditor.Appearance.Fonts;
using PdfEditor.Document.Annotations.Patching;

namespace PdfEditor.Appearance.Tests.Fonts;

/// <summary>M5 Task 7: 대체 글꼴 선택 규칙(C11), fsType 확인, 번들 글꼴 라이선스·크기.</summary>
public class FontCatalogTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string FontFile(string n) => Path.Combine(Root(), "assets", "fonts", n);

    [Fact]
    public void SerifFlag_ChoosesMyeongjo() =>
        Assert.Equal(AppFontFamily.Serif, AppFontCatalog.Classify(new PdfFontInfo("Custom", Flags: PdfFontInfo.FlagSerif)).Family);

    [Theory]
    [InlineData("ABCDEF+Batang")]
    [InlineData("TimesNewRomanPSMT")]
    [InlineData("HYMyeongJo-Medium")]
    [InlineData("바탕체")]
    [InlineData("궁서")]
    [InlineData("NotoSerifCJKkr-Regular")]
    public void NameContainsBatang_ChoosesMyeongjo(string name) =>
        Assert.Equal(AppFontFamily.Serif, AppFontCatalog.Classify(new PdfFontInfo(name)).Family);

    [Theory]
    [InlineData("ArialMT")]
    [InlineData("MalgunGothic")]
    [InlineData("Century Gothic")]
    [InlineData("Helvetica")]
    [InlineData("Flamingo")]      // 'ming' 같은 짧은 조각으로 명조를 고르지 않는다
    [InlineData("")]
    public void DefaultSans(string name)
    {
        var (family, bold, italic) = AppFontCatalog.Classify(new PdfFontInfo(name));
        Assert.Equal(AppFontFamily.Sans, family);
        Assert.False(bold);
        Assert.False(italic);
        Assert.Equal(AppFontFamily.Sans, AppFontCatalog.Classify(null).Family);
    }

    [Theory]
    [InlineData("Arial-BoldMT", 0, 0, false, true)]
    [InlineData("Arial", 0, 700, false, true)]
    [InlineData("Arial", PdfFontInfo.FlagForceBold, 0, true, true)]
    [InlineData("맑은 고딕 굵게", 0, 0, false, true)]
    [InlineData("Arial", 0, 400, false, false)]
    public void Bold_Detected(string name, int flags, int weight, bool forceBold, bool expected) =>
        Assert.Equal(expected, AppFontCatalog.Classify(new PdfFontInfo(name, flags, weight, 0, forceBold)).Bold);

    [Theory]
    [InlineData("Arial-ItalicMT", 0, 0.0, true)]
    [InlineData("Helvetica-Oblique", 0, 0.0, true)]
    [InlineData("Arial", PdfFontInfo.FlagItalic, 0.0, true)]
    [InlineData("Arial", 0, -12.0, true)]
    [InlineData("Arial", 0, 0.0, false)]
    public void Italic_Detected(string name, int flags, double angle, bool expected) =>
        Assert.Equal(expected, AppFontCatalog.Classify(new PdfFontInfo(name, flags, 0, angle)).Italic);

    [Fact]
    public void Choose_SerifMissing_FallsBackToSans_AndSaysSo()
    {
        using var sans = AppFont.Load(FontFile("NotoSansKR-Regular.otf"));
        var c = new AppFontCatalog(sans).Choose(new PdfFontInfo("Batang"));
        Assert.Equal(AppFontFamily.Sans, c.Family);
        Assert.True(c.FellBack);
        Assert.Same(sans, c.Font);
    }

    [Fact]
    public void Choose_WithSerifFont_UsesIt()
    {
        using var sans = AppFont.Load(FontFile("NotoSansKR-Regular.otf"));
        using var serif = AppFont.Load(FontFile("NotoSerifKR-Regular.otf"));
        var c = new AppFontCatalog(sans, () => serif).Choose(new PdfFontInfo("Batang-Bold"));
        Assert.Equal(AppFontFamily.Serif, c.Family);
        Assert.Same(serif, c.Font);
        Assert.True(c.SyntheticBold);
        Assert.False(c.FellBack);
    }

    [Fact]
    public void ForBaseDirectory_ReadsSerifOnlyWhenNeeded_AndFallsBackWhenMissing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pdfeditor-cat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var sans = AppFont.Load(FontFile("NotoSansKR-Regular.otf"));
            var cat = AppFontCatalog.ForBaseDirectory(sans, dir);
            Assert.Null(cat.Serif);
            Assert.True(cat.Choose(new PdfFontInfo("Batang")).FellBack);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- fsType ----

    private static byte[] FakeFont(ushort fsType, bool withOs2 = true)
    {
        // sfnt 머리 + 표 목록 1개 + OS/2(10바이트 이상). 실제 글꼴이 아니라 fsType 읽기만 검사한다.
        var d = new byte[12 + 16 + 64];
        d[0] = (byte)'O'; d[1] = (byte)'T'; d[2] = (byte)'T'; d[3] = (byte)'O';
        d[5] = 1;
        string tag = withOs2 ? "OS/2" : "head";
        for (int i = 0; i < 4; i++) d[12 + i] = (byte)tag[i];
        d[12 + 11] = 28;   // offset 28
        d[28 + 8] = (byte)(fsType >> 8); d[28 + 9] = (byte)fsType;
        return d;
    }

    [Theory]
    [InlineData(0x0000, true)]     // 제한 없는 포함
    [InlineData(0x0008, true)]     // 편집 가능 포함
    [InlineData(0x0002, false)]    // 포함 금지
    [InlineData(0x0004, false)]    // 미리보기·인쇄만
    [InlineData(0x0100, false)]    // 서브셋 금지
    [InlineData(0x0200, false)]    // 비트맵만
    [InlineData(0x0108, false)]    // 편집 가능이어도 서브셋 금지
    [InlineData(0x000A, false)]    // 모순된 비트 조합은 거부
    public void FsTypeRestricted_NotEmbedded(int fsType, bool allowed) =>
        Assert.Equal(allowed, AppFontCatalog.FsTypeAllowsEmbedding(FakeFont((ushort)fsType)));

    [Fact]
    public void FsType_NoOs2OrGarbage_NotEmbedded()
    {
        Assert.False(AppFontCatalog.FsTypeAllowsEmbedding(FakeFont(0, withOs2: false)));
        Assert.False(AppFontCatalog.FsTypeAllowsEmbedding([]));
        Assert.False(AppFontCatalog.FsTypeAllowsEmbedding([1, 2, 3]));
    }

    [Theory]
    [InlineData("NotoSansKR-Regular.otf")]
    [InlineData("NotoSerifKR-Regular.otf")]
    public void BundledFonts_FsTypeAllowsEmbedding(string file)
    {
        byte[] data = File.ReadAllBytes(FontFile(file));
        Assert.Equal(0, AppFontCatalog.ReadFsType(data));
        Assert.True(AppFontCatalog.FsTypeAllowsEmbedding(data));
    }

    // ---- 번들·라이선스·패키지 ----

    [Fact]
    public void BundledFontOnlyOfl()
    {
        string root = Root();
        string ofl = File.ReadAllText(Path.Combine(root, "assets", "fonts", "OFL.txt"));
        Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", ofl);
        // assets/fonts 의 모든 글꼴 파일은 이 OFL 하나와 THIRD-PARTY-NOTICES.md 항목이 있어야 한다
        string notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.md"));
        foreach (var f in Directory.GetFiles(Path.Combine(root, "assets", "fonts")).Where(f => f.EndsWith(".otf") || f.EndsWith(".ttf")))
        {
            string name = Path.GetFileName(f);
            Assert.Contains(name, notices);
        }
        Assert.Contains("Noto Serif KR", notices);
        Assert.Contains("OFL-1.1", notices);
        Assert.DoesNotContain("GPL", ofl.Split('\n').First());
    }

    [Fact]
    public void PackageContainsLicenses()
    {
        string root = Root();
        string pkg = File.ReadAllText(Path.Combine(root, "build", "package.ps1"));
        Assert.Contains("assets/fonts/NotoSerifKR-Regular.otf", pkg);
        Assert.Contains("assets/fonts/NotoSansKR-Regular.otf", pkg);
        Assert.Contains("assets/fonts/OFL.txt", pkg);
        string csproj = File.ReadAllText(Path.Combine(root, "src", "PdfEditor.App", "PdfEditor.App.csproj"));
        Assert.Contains("NotoSerifKR-Regular.otf", csproj);
        Assert.Contains("fonts\\OFL.txt", csproj);
        Assert.EndsWith("NotoSerifKR-Regular.otf", AppFontCatalog.SerifRelativePath);
    }

    [Fact]
    public void SubsetSizeReasonable_KoreanParagraph()
    {
        // 실제 한글 문단 한 개 정도(서로 다른 글자 약 150개)를 서브셋했을 때 글꼴이 PDF를 키우는 양
        const string para =
            "이 문서는 설계 변경 사항을 정리한 것으로 모든 도면과 시방서의 내용은 최종 승인 이후에만 효력이 있습니다. " +
            "작업자는 안전 수칙을 반드시 지켜야 하며 장비를 사용하기 전에 점검 결과를 기록하고 담당자의 서명을 받아야 합니다. " +
            "자재의 규격 수량 납기는 계약서에 따르고 변경이 필요한 경우 발주처와 협의하여 서면으로 확정합니다. " +
            "공사 중 발생한 문제는 즉시 보고하고 원인을 분석하여 재발 방지 대책을 수립한 뒤 관련 부서에 공유합니다. ABC abc 0123.";
        var cps = para.EnumerateRunes().Select(r => r.Value).Distinct().ToArray();
        Assert.InRange(cps.Length, 100, 400);
        foreach (var file in new[] { "NotoSansKR-Regular.otf", "NotoSerifKR-Regular.otf" })
        {
            byte[] sub = FontSubsetter.Subset(File.ReadAllBytes(FontFile(file)), cps);
            output.WriteLine($"SUBSET {file}: {sub.Length} bytes for {cps.Length} chars");
            Assert.True(sub.Length < 300 * 1024, $"{file}: subset {sub.Length / 1024} KB for {cps.Length} chars");
        }
    }

    [Fact]
    public void SerifFamily_EmbedsAsNotoSerifKR_WithSerifFlag()
    {
        using var serif = AppFont.Load(FontFile("NotoSerifKR-Regular.otf"));
        var (use, _) = FontTestSupport.UseFor(serif, "명조 Serif");
        use = use with { Family = FontUse.SerifFamily };
        using var doc = new PdfSharp.Pdf.PdfDocument();
        doc.AddPage();
        var type0 = (PdfSharp.Pdf.PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)FontEmbedder.Embed(doc, serif, use)).Value;
        Assert.Contains("+NotoSerifKR-Regular", type0.Elements.GetName("/BaseFont"));
    }
}
