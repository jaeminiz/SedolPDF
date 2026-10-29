using System.Globalization;

namespace PdfEditor.App.Tests.I18n;

/// <summary>현재 스레드의 화면 문화권을 잠시 바꾸고 끝나면 되돌린다. 헤드리스 UI 스레드 안에서 쓴다.</summary>
public sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _old = CultureInfo.CurrentUICulture;
    private readonly CultureInfo? _oldDefault = CultureInfo.DefaultThreadCurrentUICulture;

    public CultureScope(string code)
    {
        var c = CultureInfo.GetCultureInfo(code);
        CultureInfo.CurrentUICulture = c;
        CultureInfo.DefaultThreadCurrentUICulture = c;
    }

    public void Dispose()
    {
        CultureInfo.CurrentUICulture = _old;
        CultureInfo.DefaultThreadCurrentUICulture = _oldDefault;
    }
}
