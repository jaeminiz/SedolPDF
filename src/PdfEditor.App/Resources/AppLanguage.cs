using System.Globalization;

namespace PdfEditor.App.Resources;

/// <summary>
/// 화면 언어 정하기(0.4.0, D13·U5). 설정 "auto"(또는 비어 있음) = 설치된 Windows 표시 언어가 한국어면 한국어, 아니면 영어.
/// 창을 만들기 전(Program.Main)에 한 번 적용하고, 바꾸면 다시 시작한 뒤부터 바뀐다.
/// </summary>
public static class AppLanguage
{
    public const string Auto = "auto";
    public const string Korean = "ko";
    public const string English = "en";

    /// <summary>설정 값을 "ko" 또는 "en"으로 풀어 준다. 모르는 값은 자동으로 본다.</summary>
    public static string Resolve(string? setting, CultureInfo installedUi) => setting switch
    {
        Korean => Korean,
        English => English,
        _ => string.Equals(installedUi.TwoLetterISOLanguageName, "ko", StringComparison.OrdinalIgnoreCase) ? Korean : English,
    };

    /// <summary>설정에 저장할 값: ko·en은 그대로, 나머지는 null(자동).</summary>
    public static string? Normalize(string? setting) => setting is Korean or English ? setting : null;

    /// <summary>선택한 언어로 화면 문구 문화권을 정한다(UI 스레드와 백그라운드 스레드 모두). 돌려주는 값은 "ko" 또는 "en".</summary>
    public static string Apply(string? setting)
    {
        string code = Resolve(setting, CultureInfo.InstalledUICulture);
        var culture = CultureInfo.GetCultureInfo(code);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        return code;
    }

    /// <summary>
    /// 사용자에게 보이는 날짜·숫자 서식 문화권. 화면 언어를 따르되 OS 지역 설정이 같은 언어이면 그것을 쓴다
    /// (예: 영어 화면 + 미국/영국 지역 설정이면 그 지역 서식, 영어 화면 + 한국 지역 설정이면 en-US).
    /// </summary>
    public static CultureInfo FormatCulture
    {
        get
        {
            string ui = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            var region = CultureInfo.CurrentCulture;
            if (string.Equals(region.TwoLetterISOLanguageName, ui, StringComparison.OrdinalIgnoreCase)) return region;
            return CultureInfo.GetCultureInfo(string.Equals(ui, "ko", StringComparison.OrdinalIgnoreCase) ? "ko-KR" : "en-US");
        }
    }
}
