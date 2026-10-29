using System.Globalization;
using System.Runtime.CompilerServices;

namespace PdfEditor.App.Tests;

/// <summary>
/// 기존 시험은 한국어 화면 문구를 기대한다. 시험 컴퓨터의 Windows 언어와 관계없이 화면 문화권을 한국어로 고정한다
/// (영어 화면을 시험하는 시험은 스스로 문화권을 바꾸고 되돌린다 — <see cref="I18n.CultureScope"/>).
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void Init()
    {
        var ko = CultureInfo.GetCultureInfo("ko");
        CultureInfo.DefaultThreadCurrentUICulture = ko;
        CultureInfo.CurrentUICulture = ko;
    }
}
