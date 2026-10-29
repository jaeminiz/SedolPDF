using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using PdfEditor.App.Resources;
using Xunit.Abstractions;

namespace PdfEditor.App.Tests.I18n;

/// <summary>영어 번역의 자동 품질 점검 (0.4.0 Task 9, D13). 한국어(기본)·영어 리소스가 같은 키와 같은 형식 인자를 갖는지 확인한다.</summary>
public class StringsTranslationTests(ITestOutputHelper output)
{
    private static readonly ResourceManager Rm = new("PdfEditor.App.Resources.Strings", typeof(Strings).Assembly);
    private static readonly Regex Placeholder = new(@"\{(\d+)(?::[^}]*)?\}", RegexOptions.Compiled);
    private static readonly Regex Hangul = new(@"[ᄀ-ᇿ㄰-㆏가-힯]", RegexOptions.Compiled);

    /// <summary>영어 리소스에 한글이 있어도 되는 키 — 언어 이름 자체(고유명사)다.</summary>
    private static readonly HashSet<string> HangulAllowed = ["Dlg_SettingsLanguageKo"];

    private static Dictionary<string, string> Load(CultureInfo culture, bool tryParents)
    {
        var set = Rm.GetResourceSet(culture, createIfNotExists: true, tryParents: tryParents)
                  ?? throw new InvalidOperationException("리소스 없음: " + culture);
        var d = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry e in set) d[(string)e.Key] = (string)e.Value!;
        return d;
    }

    private static Dictionary<string, string> Ko => Load(CultureInfo.InvariantCulture, true);
    private static Dictionary<string, string> En => Load(CultureInfo.GetCultureInfo("en"), false);

    [Fact]
    public void EnglishAndKorean_HaveSameKeySet()
    {
        var ko = Ko; var en = En;
        Assert.Empty(ko.Keys.Except(en.Keys));
        Assert.Empty(en.Keys.Except(ko.Keys));
        Assert.True(ko.Count > 500);
    }

    [Fact]
    public void StringsAccessors_MatchResourceKeys()
    {
        var props = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string)).Select(p => p.Name).ToHashSet();
        Assert.Empty(Ko.Keys.Except(props));
        Assert.Empty(props.Except(Ko.Keys));
    }

    [Fact]
    public void FormatPlaceholders_MatchBetweenLanguages()
    {
        var ko = Ko; var en = En;
        var bad = new List<string>();
        foreach (var (k, v) in ko)
        {
            var a = Placeholder.Matches(v).Select(m => m.Groups[1].Value).ToHashSet();
            var b = Placeholder.Matches(en[k]).Select(m => m.Groups[1].Value).ToHashSet();
            if (!a.SetEquals(b)) bad.Add($"{k}: ko={string.Join(',', a)} en={string.Join(',', b)}");
        }
        Assert.Empty(bad);
    }

    [Fact]
    public void NoEmptyText_InEitherLanguage()
    {
        Assert.Empty(Ko.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key));
        Assert.Empty(En.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key));
    }

    [Fact]
    public void EnglishText_HasNoHangul_ExceptLanguageNames()
    {
        var bad = En.Where(p => Hangul.IsMatch(p.Value) && !HangulAllowed.Contains(p.Key)).Select(p => p.Key).ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void EnglishText_IsActuallyTranslated_NotKoreanCopy()
    {
        // 한국어와 똑같은 문구는 번역 누락이다(이름·주소·언어 이름 제외).
        var ko = Ko; var en = En;
        var same = ko.Where(p => en[p.Key] == p.Value && Regex.IsMatch(p.Value, @"[A-Za-z가-힯]{3,}")).Select(p => p.Key)
            .Where(k => k is not ("App_Name" or "About_Authors" or "About_Contact1" or "About_Contact2" or "About_Copyright" or "Dlg_SettingsLanguageEn" or "Dlg_SettingsLanguageKo")).ToList();
        Assert.Empty(same);
    }

    [Fact]
    public void LongTranslations_AreListed_ForLayoutReview()
    {
        // 한글은 글자 하나가 영어 알파벳 둘 너비라 보고 비교한다. 2.5배 이상이면 단추·리본 후보로 눈으로 점검한다(실패 조건은 28자 초과뿐).
        static double Width(string s) => s.Sum(c => Hangul.IsMatch(c.ToString()) ? 2.0 : 1.0);
        var ko = Ko; var en = En;
        var longOnes = ko.Where(p => p.Key.StartsWith("Cmd_") || p.Key.StartsWith("Group_") || p.Key.StartsWith("Tab_"))
            .Select(p => (p.Key, Ko: p.Value, En: en[p.Key], Ratio: Width(en[p.Key]) / Math.Max(1, Width(p.Value))))
            .Where(x => x.Ratio >= 2.5).OrderByDescending(x => x.Ratio).ToList();
        foreach (var x in longOnes) output.WriteLine($"{x.Ratio:0.0}x {x.Key}: {x.Ko} -> {x.En}");
        Assert.All(longOnes, x => Assert.True(x.En.Length <= 28, x.Key));
    }

    [Fact]
    public void MissingTranslation_FallsBackToKorean()
    {
        // 위성이 없는 언어(예: 일본어)는 한국어(기본) 문구가 나온다.
        string ja = Rm.GetString("Tab_File", CultureInfo.GetCultureInfo("ja"))!;
        Assert.Equal(Ko["Tab_File"], ja);
    }

    [Fact]
    public void Strings_FollowCurrentUiCulture()
    {
        using (new CultureScope("ko")) Assert.Equal("파일", Strings.Tab_File);
        using (new CultureScope("en")) Assert.Equal("File", Strings.Tab_File);
    }

    [Fact]
    public void Glossary_KeyTermsAreUsedConsistently()
    {
        // 용어집(docs/i18n/glossary.md)의 핵심 용어를 한 가지로만 쓴다.
        var en = En;
        Assert.Equal("Comment", en["Tab_Annotate"]);
        Assert.Equal("Pages", en["Tab_Page"]);
        Assert.DoesNotContain(en, p => Regex.IsMatch(p.Value, @"\b(Memo|Remark)\b"));
        Assert.DoesNotContain(en, p => Regex.IsMatch(p.Value, @"\bsheets?\b", RegexOptions.IgnoreCase));
        Assert.DoesNotContain(en, p => Regex.IsMatch(p.Value, @"\bhull number\b", RegexOptions.IgnoreCase));
    }
}
