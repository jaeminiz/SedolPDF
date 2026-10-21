using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using PdfEditor.App.Shell;
using PdfEditor.App.Tests.Viewer.Annotations;
using Xunit;

namespace PdfEditor.App.Tests.Shell;

/// <summary>탭이 아주 많아 넘칠 때의 좌우 스크롤(2026-10-21 사용자 요청).</summary>
public class TabStripScrollTests
{
    [Theory]
    [InlineData(0, 500, 1000, 600, 700, 200)]    // 오른쪽에 숨은 탭: 오른쪽 끝이 보이게 (700-500)
    [InlineData(300, 500, 1000, 100, 200, 100)]  // 왼쪽에 숨은 탭: 왼쪽 끝이 보이게
    [InlineData(100, 500, 1000, 200, 300, 100)]  // 이미 보이면 그대로
    [InlineData(50, 500, 400, 0, 100, 0)]        // 넘치지 않으면 0
    public void RevealOffset_MovesJustEnough(double offset, double avail, double total, double left, double right, double expected)
        => Assert.Equal(expected, TabStripPanel.RevealOffset(offset, avail, total, left, right));

    private static bool _themed;

    private static (TabStripPanel Panel, ListBox List, Window Win) Build(int count, double width)
    {
        if (!_themed) { Application.Current!.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme()); _themed = true; }
        var list = new ListBox { ItemsPanel = new FuncTemplate<Panel?>(() => new TabStripPanel()), Width = width, Height = 30 };
        for (int i = 0; i < count; i++) list.Items.Add(new ListBoxItem { Content = "탭 " + i });
        var win = new Window { Width = width + 20, Height = 80, Content = list };
        win.Show();
        win.UpdateLayout();
        var panel = list.GetVisualDescendants().OfType<TabStripPanel>().First();
        return (panel, list, win);
    }

    [Fact]
    public void ManyTabs_Overflow_ScrollButtonsStateFollowsOffset()
    {
        HeadlessUi.Run(() =>
        {
            var (p, list, win) = Build(30, 600);
            win.UpdateLayout();
            Assert.True(p.IsOverflowing);                 // 30 × 72 > 600
            Assert.True(p.MaxOffset > 0);
            Assert.False(p.CanScrollLeft);                // 맨 왼쪽에서 시작
            p.ScrollBy(p.PageStep);
            win.UpdateLayout();
            Assert.True(p.CanScrollLeft && p.CanScrollRight);
            p.ScrollBy(100000);                           // 끝까지
            win.UpdateLayout();
            Assert.False(p.CanScrollRight);
            Assert.Equal(p.MaxOffset, p.Offset, 1);
            p.ScrollBy(-100000);
            Assert.Equal(0, p.Offset, 1);
            win.Close();
            return 0;
        });
    }

    [Fact]
    public void FewTabs_DoNotOverflow()
    {
        HeadlessUi.Run(() =>
        {
            var (p, _, win) = Build(3, 600);
            Assert.False(p.IsOverflowing);
            p.ScrollBy(100);
            Assert.Equal(0, p.Offset);
            win.Close();
            return 0;
        });
    }

    [Fact]
    public void SelectingAHiddenTab_ScrollsItIntoView()
    {
        HeadlessUi.Run(() =>
        {
            var (p, list, win) = Build(30, 600);
            list.SelectedIndex = 29;
            win.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            win.UpdateLayout();
            Assert.Equal(p.MaxOffset, p.Offset, 1);       // 마지막 탭이 보이게 오른쪽 끝까지
            list.SelectedIndex = 0;
            win.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            win.UpdateLayout();
            Assert.Equal(0, p.Offset, 1);
            win.Close();
            return 0;
        });
    }
}
