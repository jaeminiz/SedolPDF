using System.Reflection;
using System.Text.RegularExpressions;
using PdfEditor.App.Resources;

namespace PdfEditor.App.Tests;

public class ResourceGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PdfEditor.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("PdfEditor.sln not found");
    }

    private static IEnumerable<string> SourceFiles(string sub, string pattern) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), sub), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void EveryStringsProperty_HasNonEmptyKoreanValue()
    {
        var props = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string)).ToList();
        Assert.NotEmpty(props);
        var bad = new List<string>();
        foreach (var p in props)
        {
            var v = (string?)p.GetValue(null);
            if (string.IsNullOrWhiteSpace(v) || v == p.Name) bad.Add(p.Name);
        }
        Assert.True(bad.Count == 0, "Empty or missing: " + string.Join(", ", bad));
        Assert.Equal("SedolPDF", Strings.App_Name);
    }

    [Fact]
    public void NoHangulLiterals_InAppSources()
    {
        var hangulString = new Regex("\"[^\"]*[가-힣][^\"]*\"");
        var hangul = new Regex("[가-힣]");
        var violations = new List<string>();

        foreach (var f in SourceFiles(Path.Combine("src", "PdfEditor.App"), "*.cs"))
        {
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//")) continue;
                if (hangulString.IsMatch(StripTrailingComment(lines[i]))) violations.Add($"{f}:{i + 1}");
            }
        }

        foreach (var f in SourceFiles(Path.Combine("src", "PdfEditor.App"), "*.axaml"))
        {
            var body = Regex.Replace(File.ReadAllText(f), "<!--.*?-->",
                m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);
            var lines = body.Split('\n');
            for (int i = 0; i < lines.Length; i++)
                if (hangul.IsMatch(lines[i])) violations.Add($"{f}:{i + 1}");
        }

        Assert.True(violations.Count == 0, "Hangul literals found: " + string.Join("; ", violations));
    }

    // 문자열 리터럴 밖의 "//" 부터 줄 끝까지를 잘라낸다.
    private static string StripTrailingComment(string line)
    {
        bool inStr = false;
        for (int i = 0; i < line.Length - 1; i++)
        {
            char c = line[i];
            if (c == '\\' && inStr) { i++; continue; }
            if (c == '"') inStr = !inStr;
            else if (!inStr && c == '/' && line[i + 1] == '/') return line[..i];
        }
        return line;
    }

    [Fact]
    public void NoBlobFromStream_InSources()
    {
        // src/ 만 검사한다 (이 파일은 tests/ 아래).
        var needle = "Blob." + "FromStream";
        var hits = SourceFiles("src", "*.cs").Where(f => File.ReadAllText(f).Contains(needle)).ToList();
        Assert.True(hits.Count == 0, "Blob.FromStream used in: " + string.Join(", ", hits));
    }
}
