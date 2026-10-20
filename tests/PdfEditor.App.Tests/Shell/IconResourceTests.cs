using System.Text.RegularExpressions;
using PdfEditor.App.Shell;
using Xunit;

namespace PdfEditor.App.Tests.Shell;

/// <summary>아이콘 자원 점검: 키가 겹치면 앱이 시작하자마자 죽고(2026-10-20 실제로 겪음), 키가 없으면 단추가 빈 네모로 보인다.</summary>
public class IconResourceTests
{
    private static string IconsXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var f = Path.Combine(dir.FullName, "src", "PdfEditor.App", "Resources", "Icons.axaml");
            if (File.Exists(f)) return File.ReadAllText(f);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Icons.axaml");
    }

    [Fact]
    public void IconKeys_AreUnique()
    {
        var keys = Regex.Matches(IconsXaml(), @"x:Key=""Icon\.([A-Za-z0-9]+)""").Select(m => m.Groups[1].Value).ToList();
        Assert.Empty(keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key));
    }

    [Fact]
    public void EveryRibbonCommand_HasAnExistingIcon()
    {
        var keys = Regex.Matches(IconsXaml(), @"x:Key=""Icon\.([A-Za-z0-9]+)""").Select(m => m.Groups[1].Value).ToHashSet();
        var vm = new MainWindowViewModel();
        var ids = vm.Ribbon.Values.SelectMany(gs => gs).SelectMany(g => g.Items).SelectMany(i => i.MoreItems is { } m ? m.Prepend(i) : [i]).Select(i => i.CommandId).Distinct();
        var missing = ids.Select(id => vm.Commands.Find(id)!).Where(c => c.IconKey is null || !keys.Contains(c.IconKey)).Select(c => $"{c.Id}:{c.IconKey ?? "(none)"}").ToList();
        Assert.True(missing.Count == 0, "No icon: " + string.Join(", ", missing));
    }
}
