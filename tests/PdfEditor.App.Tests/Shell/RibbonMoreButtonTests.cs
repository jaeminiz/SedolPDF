using PdfEditor.App.Documents;
using PdfEditor.App.Shell;
using PdfEditor.App.Shell.Ribbon;
using PdfEditor.App.Tests.Viewer.Annotations;

namespace PdfEditor.App.Tests.Shell;

public class RibbonMoreButtonTests
{
    [Fact]
    public void MoreButton_BuildsMenuWithCheckMarkOnActiveTool()
    {
        var vm = new MainWindowViewModel();
        var d = new DocumentTabViewModel("a.pdf");
        vm.Documents.Add(d);
        vm.ActiveDocument = d;
        vm.SelectedRibbonTab = "Annotate";
        var more = vm.CurrentRibbonGroups.SelectMany(g => g.Items).Single(b => b.IsMore);
        d.Tool = ToolKind.Polygon;

        var menu = HeadlessUi.Run(() =>
        {
            var btn = new RibbonMoreButton { Label = more.Label, Items = more.MoreItems };
            btn.Rebuild();
            return btn.MenuItems.Select(m => (Header: m.Header?.ToString(), m.IsChecked)).ToList();
        });
        Assert.Equal(12, menu.Count);   // 도구 10개 + 코멘트 목록 + XFDF 내보내기
        Assert.Equal(more.MoreItems!.Select(i => i.Label), menu.Select(m => m.Header));
        Assert.Equal([PdfEditor.App.Resources.Strings.Cmd_Polygon], menu.Where(m => m.IsChecked).Select(m => m.Header));
    }
}
