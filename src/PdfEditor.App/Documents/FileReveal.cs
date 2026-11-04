using System.Diagnostics;

namespace PdfEditor.App.Documents;

/// <summary>탭의 "포함하는 폴더 열기": 탐색기를 열어 그 파일을 선택한 채로 보인다. 경로에 공백·한글이 있어도 되게 인자를 따옴표로 감싼다.</summary>
public static class FileReveal
{
    /// <summary>탐색기 인자(<c>/select,"경로"</c>). 경로가 없거나 파일이 없으면 null(폴더 열기를 할 수 없다).</summary>
    public static string? BuildArguments(string? path, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('"')) return null;
        if (!(fileExists ?? File.Exists)(path)) return null;
        return "/select,\"" + Path.GetFullPath(path) + "\"";
    }

    /// <summary>탐색기를 연다. 열었으면 true. 열 수 없으면(경로 없음 등) false.</summary>
    public static bool Reveal(string? path)
    {
        if (BuildArguments(path) is not { } args) return false;
        try { Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true }); return true; }
        catch (Exception) { return false; }
    }
}
