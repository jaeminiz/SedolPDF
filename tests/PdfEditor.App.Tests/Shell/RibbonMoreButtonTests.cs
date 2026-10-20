using PdfEditor.App.Shell;
using PdfEditor.App.Shell.Ribbon;
using PdfEditor.App.Tests.Viewer.Annotations;

namespace PdfEditor.App.Tests.Shell;

public class RibbonMoreButtonTests
{
    [Fact]
    public void MoreButton_BuildsMenuWithCheckMarkOnActiveTool()
    {
        // 주석 탭은 2026-10-20부터 "더보기"를 쓰지 않는다. 단추 자체는 남겨 두고 직접 만든 항목으로 확인한다.
        var vm = new MainWindowViewModel();
        RibbonButtonVm T(string id, string label) => new(vm.Commands.Find(id)!, label, false, null);
        List<RibbonButtonVm> items = [T("annot.rect", "사각형"), T("annot.polygon", "다각형"), T("annot.cloud", "클라우드")];
        items[1].IsChecked = true;

        var menu = HeadlessUi.Run(() =>
        {
            var btn = new RibbonMoreButton { Label = "더보기", Items = items };
            btn.Rebuild();
            return btn.MenuItems.Select(m => (Header: m.Header?.ToString(), m.IsChecked)).ToList();
        });
        Assert.Equal(items.Select(i => i.Label), menu.Select(m => m.Header));
        Assert.Equal(["다각형"], menu.Where(m => m.IsChecked).Select(m => m.Header));
    }
}
