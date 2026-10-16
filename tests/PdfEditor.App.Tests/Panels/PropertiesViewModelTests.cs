using System.Text.RegularExpressions;
using PdfEditor.App.Panels;
using PdfEditor.App.Resources;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Panels;

public class PropertiesViewModelTests : PanelTestBase
{
    [Fact]
    public async Task Properties_ShowsVersionAndPermissions()
    {
        string p = Path.Combine(Dir, "r.pdf");
        TestPdfFactory.CreateOwnerRestricted(p, allowPrint: false, allowModify: true, allowCopy: true);
        var vm = new PropertiesViewModel(await OpenTab(p));

        await vm.LoadAsync();

        Assert.Equal("r.pdf", vm.FileName);
        Assert.Equal(1, vm.PageCount);
        Assert.Matches(new Regex(@"^\d\.\d$"), vm.PdfVersion);
        Assert.Equal(Strings.Prop_Denied, vm.PrintPermission);
        Assert.Equal(Strings.Prop_Allowed, vm.EditPagesPermission);
        Assert.Equal(Strings.Prop_Allowed, vm.CopyPermission);
        Assert.Equal(Strings.Prop_Yes, vm.Encrypted);
        Assert.Equal(Strings.Prop_No, vm.Repaired);
    }

    [Fact]
    public async Task Properties_Unencrypted_AllAllowed_AndPageSizeFollowsCurrentPage()
    {
        string p = Path.Combine(Dir, "u.pdf");
        TestPdfFactory.CreateLabeled(p, [(595, 842), (842, 595)]);
        var tab = await OpenTab(p);
        var vm = new PropertiesViewModel(tab);
        await vm.LoadAsync();

        Assert.Equal(Strings.Prop_No, vm.Encrypted);
        Assert.Equal(Strings.Prop_Allowed, vm.PrintPermission);
        Assert.Equal(string.Format(Strings.Prop_PageSizeValue, 595 * 25.4 / 72, 842 * 25.4 / 72), vm.PageSizeText);

        tab.CurrentPageIndex = 1;
        Assert.Equal(string.Format(Strings.Prop_PageSizeValue, 842 * 25.4 / 72, 595 * 25.4 / 72), vm.PageSizeText);
    }

    [Theory]
    [InlineData(17, "1.7")]
    [InlineData(14, "1.4")]
    [InlineData(20, "2.0")]
    public void FormatVersion_TenthsToDotted(int raw, string expected) =>
        Assert.Equal(expected, PropertiesViewModel.FormatVersion(raw));

    [Fact]
    public void FormatVersion_Null_IsDash() => Assert.Equal(Strings.Prop_Unknown, PropertiesViewModel.FormatVersion(null));

    [Fact]
    public async Task Properties_EmptyMetadata_ShowsDash()
    {
        string p = Path.Combine(Dir, "m.pdf");
        TestPdfFactory.CreateBlank(p, 1, 595, 842);
        var vm = new PropertiesViewModel(await OpenTab(p));
        await vm.LoadAsync();
        Assert.False(string.IsNullOrEmpty(vm.Title));   // 비어 있으면 대시를 보인다
    }
}
