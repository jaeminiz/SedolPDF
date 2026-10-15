using System.Reflection;
using System.Resources;

[assembly: NeutralResourcesLanguage("ko")]

namespace PdfEditor.App.Resources;

/// <summary>화면 문구 접근자. 키 이름 규칙: App_ Tab_ Group_ Cmd_ Panel_ Status_ Dlg_ Msg_ Err_. 문구는 Strings.resx에만 둔다.</summary>
public static class Strings
{
    private static readonly ResourceManager Rm =
        new("PdfEditor.App.Resources.Strings", typeof(Strings).Assembly);

    private static string Get(string key) => Rm.GetString(key) ?? key;

    public static string App_Name => Get(nameof(App_Name));
    public static string Tab_File => Get(nameof(Tab_File));
    public static string Tab_Home => Get(nameof(Tab_Home));
    public static string Tab_Annotate => Get(nameof(Tab_Annotate));
    public static string Tab_Edit => Get(nameof(Tab_Edit));
    public static string Tab_Page => Get(nameof(Tab_Page));
    public static string Tab_View => Get(nameof(Tab_View));
}
