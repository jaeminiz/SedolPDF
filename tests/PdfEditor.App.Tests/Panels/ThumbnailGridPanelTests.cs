using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfEditor.App.Panels;
using PdfEditor.App.Tests.Viewer.Annotations;

namespace PdfEditor.App.Tests.Panels;

/// <summary>여러 열 썸네일 패널을 실제 ListBox 안에서 돌려 본다(가상화·열 수·스크롤·키보드).</summary>
public class ThumbnailGridPanelTests
{
    private sealed record Harness(Window Window, ListBox List, ThumbnailGridPanel Panel);

    private static Harness Show(int count, double width, double height = 500)
    {
        if (Application.Current is { } app && !app.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>().Any())
            app.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        var list = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            ItemsSource = Enumerable.Range(1, count).Select(i => "P" + i).ToList(),
            ItemsPanel = new FuncTemplate<Panel?>(() => new ThumbnailGridPanel { CellWidth = 100, CellHeight = 80 }),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        var w = new Window { Width = width, Height = height, Content = list };
        w.Show();
        Pump(w);
        return new Harness(w, list, (ThumbnailGridPanel)list.ItemsPanelRoot!);
    }

    private static void Pump(Window w)
    {
        for (int i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); }
    }

    [Fact]
    public void WideList_ShowsSeveralColumns_AndOnlyRealizesWhatIsVisible()
    {
        HeadlessUi.Run(() =>
        {
            var h = Show(600, 330);   // 스크롤 막대를 빼고도 3열(100 × 3)
            Assert.True(h.Panel.Columns >= 3, $"columns {h.Panel.Columns}");
            Assert.True(h.Panel.Children.Count < 60, $"만든 칸 {h.Panel.Children.Count}");
            Assert.True(h.Panel.Children.Count >= h.Panel.Columns * 5);   // 보이는 줄(500/80≈6줄)은 채운다
            // 같은 줄의 칸들은 y가 같고 x가 늘어난다
            var first = h.Panel.Children.OfType<ListBoxItem>().OrderBy(c => c.Bounds.Y).ThenBy(c => c.Bounds.X).Take(3).ToList();
            Assert.Equal(first[0].Bounds.Y, first[1].Bounds.Y);
            Assert.True(first[1].Bounds.X > first[0].Bounds.X);
            h.Window.Close();
            return 0;
        });
    }

    [Fact]
    public void NarrowList_IsOneColumn()
    {
        HeadlessUi.Run(() =>
        {
            var h = Show(50, 130);
            Assert.Equal(1, h.Panel.Columns);
            h.Window.Close();
            return 0;
        });
    }

    [Fact]
    public void ScrollIntoView_FarItem_RealizesIt_AndStillFewContainers()
    {
        HeadlessUi.Run(() =>
        {
            var h = Show(600, 330);
            h.List.ScrollIntoView(450);
            Pump(h.Window);
            Assert.NotNull(h.List.ContainerFromIndex(450));
            Assert.True(h.Panel.Children.Count < 60, $"만든 칸 {h.Panel.Children.Count}");
            var sv = h.List.FindDescendantOfType<ScrollViewer>()!;
            Assert.True(sv.Offset.Y > 80 * (450 / h.Panel.Columns) - 500, $"offset {sv.Offset.Y}");
            h.Window.Close();
            return 0;
        });
    }

    [Fact]
    public void ArrowKeys_MoveByColumnsAndRows()
    {
        HeadlessUi.Run(() =>
        {
            var h = Show(100, 330);
            int cols = h.Panel.Columns;
            h.List.SelectedIndex = 0;
            Pump(h.Window);
            h.List.ContainerFromIndex(0)!.Focus();
            h.Window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Pump(h.Window);
            Assert.Equal(1, h.List.SelectedIndex);
            h.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Pump(h.Window);
            Assert.Equal(1 + cols, h.List.SelectedIndex);
            h.Window.Close();
            return 0;
        });
    }

    [Fact]
    public void ItemsChange_KeepsLayoutAndSelectionConsistent()
    {
        HeadlessUi.Run(() =>
        {
            var h = Show(30, 330);
            var src = new System.Collections.ObjectModel.ObservableCollection<string>(Enumerable.Range(1, 30).Select(i => "P" + i));
            h.List.ItemsSource = src;
            Pump(h.Window);
            h.List.SelectedItems!.Add(src[2]);
            src.RemoveAt(0);
            Pump(h.Window);
            Assert.Equal(29, src.Count);
            Assert.Contains("P3", h.List.SelectedItems!.Cast<string>());
            Assert.Equal(src.IndexOf("P3"), h.List.SelectedIndex);
            h.Window.Close();
            return 0;
        });
    }
}
