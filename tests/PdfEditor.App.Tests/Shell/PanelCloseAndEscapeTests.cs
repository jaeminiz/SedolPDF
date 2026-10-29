using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Resources;
using PdfEditor.App.Settings;
using PdfEditor.App.Shell;
using PdfEditor.App.Shell.Ribbon;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.App.Viewer;
using Xunit;

namespace PdfEditor.App.Tests.Shell;

/// <summary>0.4.0 Task 7~8: 패널 쉽게 닫기, Esc 한 곳(EscapePolicy), 손바닥 아이콘·커서, 홈 탭 기본 주석 도구.</summary>
[Collection("TabSettings")]
public class PanelCloseAndEscapeTests
{
    // ---- EscapePolicy (표) ----

    [Theory]
    [InlineData(ToolKind.Rectangle, true, false, false, EscapeAction.CancelDrawing)]    // 그리는 중 → 취소만
    [InlineData(ToolKind.Rectangle, true, true, false, EscapeAction.CancelDrawing)]     // 선택이 있어도 그리는 중이면 취소가 먼저
    [InlineData(ToolKind.Rectangle, false, true, false, EscapeAction.ClearSelection)]   // 선택 → 해제(도구는 그대로)
    [InlineData(ToolKind.EditObject, false, true, false, EscapeAction.ClearSelection)]
    [InlineData(ToolKind.Rectangle, false, false, false, EscapeAction.SwitchToAnnotSelect)]
    [InlineData(ToolKind.Hand, false, false, false, EscapeAction.SwitchToAnnotSelect)]
    [InlineData(ToolKind.TextSelect, false, false, false, EscapeAction.SwitchToAnnotSelect)]
    [InlineData(ToolKind.EditText, false, false, false, EscapeAction.SwitchToAnnotSelect)]
    [InlineData(ToolKind.AnnotSelect, false, false, false, EscapeAction.None)]          // 이미 주석 선택 도구이고 선택도 없다
    [InlineData(ToolKind.Rectangle, true, true, true, EscapeAction.None)]               // 편집 창·입력칸이 포커스를 가지면 가로채지 않는다
    [InlineData(ToolKind.Hand, false, false, true, EscapeAction.None)]
    public void EscapePolicy_Table(ToolKind tool, bool drawing, bool selected, bool editor, EscapeAction expected)
        => Assert.Equal(expected, EscapePolicy.Decide(tool, drawing, selected, editor));

    [Fact]
    public void EscapePressed_OnTab_FollowsPolicy_InThreeSteps()
    {
        var tab = new DocumentTabViewModel("a.pdf") { Tool = ToolKind.Hand };
        Assert.Equal(EscapeAction.SwitchToAnnotSelect, tab.EscapePressed());
        Assert.Equal(ToolKind.AnnotSelect, tab.Tool);
        Assert.Equal(EscapeAction.None, tab.EscapePressed());                      // 더 할 일이 없으면 키를 가로채지 않는다
        tab.Tool = ToolKind.Ellipse;
        Assert.Equal(EscapeAction.CancelDrawing, tab.EscapePressed(viewDrawing: true));
        Assert.Equal(ToolKind.Ellipse, tab.Tool);                                   // 그리는 중 취소는 도구를 바꾸지 않는다
        Assert.Equal(EscapeAction.None, tab.EscapePressed(textEditorFocused: true));
        Assert.Equal(ToolKind.Ellipse, tab.Tool);
        Assert.Equal(EscapeAction.SwitchToAnnotSelect, tab.EscapePressed());
    }

    [Fact]
    public void EscapePolicy_IsTheOnlyEscHandlerInTheDocumentView()
    {
        // V27 표: 문서 화면(DocumentView*.cs)의 Esc 처리는 HandleEscapeKey 한 곳뿐이다(편집 창·패널 입력칸은 자기 파일에서 따로).
        var root = RepoRoot();
        var hits = Directory.GetFiles(Path.Combine(root, "src", "PdfEditor.App", "Viewer"), "DocumentView*.cs")
            .Where(f => File.ReadAllText(f).Contains("Key.Escape")).Select(Path.GetFileName).ToList();
        Assert.Equal(["DocumentView.Annotate.cs"], hits);
    }

    // ---- 패널 ----

    [Fact]
    public void PanelCloseButton_HidesPanel()
    {
        var vm = new MainWindowViewModel();
        Assert.True(vm.IsLeftPanelVisible);
        vm.CloseLeftPanelCommand.Execute(null);
        Assert.False(vm.IsLeftPanelVisible);
        Assert.True(vm.IsPropertiesPanelVisible);
        vm.ClosePropertiesPanelCommand.Execute(null);
        Assert.False(vm.IsPropertiesPanelVisible);
        // 화면 연결: ✕ 단추가 두 패널 머리에 있고 같은 명령을 부른다
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PdfEditor.App", "Shell", "MainWindow.axaml"));
        Assert.Contains("Command=\"{Binding CloseLeftPanelCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding ClosePropertiesPanelCommand}\"", xaml);
    }

    [Fact]
    public void PanelHeaderDoubleClick_Collapses()
    {
        // 더블클릭 처리기는 패널 닫기 명령을 부른다(헤드리스 합성 더블클릭은 환경 의존이라 연결을 소스로 확인한다).
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PdfEditor.App", "Shell", "MainWindow.axaml"));
        Assert.Contains("x:Name=\"LeftPanelHeader\"", xaml);
        Assert.Contains("DoubleTapped=\"OnLeftPanelHeaderDoubleTapped\"", xaml);
        Assert.Contains("DoubleTapped=\"OnPropertiesPanelHeaderDoubleTapped\"", xaml);
        var cs = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PdfEditor.App", "Shell", "MainWindow.axaml.cs"));
        Assert.Contains("OnLeftPanelHeaderDoubleTapped(object? sender, TappedEventArgs e) => _vm.CloseLeftPanelCommand.Execute(null)", cs);
    }

    [Fact]
    public void ActivityIconSecondClick_Collapses()
    {
        var vm = new MainWindowViewModel();
        vm.SetPanelCommand.Execute(PanelKind.Comments);
        Assert.True(vm.IsLeftPanelVisible);
        Assert.Equal(PanelKind.Comments, vm.ActiveLeftPanel);
        vm.SetPanelCommand.Execute(PanelKind.Comments);                  // 두 번째 클릭 = 접기
        Assert.False(vm.IsLeftPanelVisible);
        vm.SetPanelCommand.Execute(PanelKind.Comments);                  // 접힌 뒤 다시 누르면 펼침
        Assert.True(vm.IsLeftPanelVisible);
        vm.SetPanelCommand.Execute(PanelKind.Layers);                    // 다른 아이콘은 바꿔 보이기
        Assert.True(vm.IsLeftPanelVisible);
        Assert.Equal(PanelKind.Layers, vm.ActiveLeftPanel);
    }

    [Fact]
    public void Shortcut_ToggleLeftPanel_AndNoConflicts()
    {
        var vm = new MainWindowViewModel();
        var left = vm.Commands.Find("view.leftPanel")!;
        var props = vm.Commands.Find("view.properties")!;
        Assert.Equal((Key.B, KeyModifiers.Control), (left.Gesture!.Key, left.Gesture.KeyModifiers));
        Assert.Equal((Key.B, KeyModifiers.Control | KeyModifiers.Shift), (props.Gesture!.Key, props.Gesture.KeyModifiers));
        left.Command.Execute(null);
        Assert.False(vm.IsLeftPanelVisible);
        left.Command.Execute(null);
        Assert.True(vm.IsLeftPanelVisible);
        props.Command.Execute(null);
        Assert.False(vm.IsPropertiesPanelVisible);
        // 충돌 점검: 같은 단축키를 가진 명령이 없다
        var dup = vm.Commands.All.Where(c => c.Gesture is not null).GroupBy(c => (c.Gesture!.Key, c.Gesture.KeyModifiers)).Where(g => g.Count() > 1).ToList();
        Assert.Empty(dup);
    }

    [Fact]
    public void PanelState_IsSavedAndRestored()
    {
        string file = Path.Combine(Path.GetTempPath(), "pdfeditor-panel-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new SettingsStore(file);
            store.Update(s => s with { LeftPanelVisible = false, LeftPanelKind = nameof(PanelKind.Comments), PropertiesPanelVisible = false });
            var vm = new MainWindowViewModel();
            vm.ApplyPanelState(new SettingsStore(file).Load());
            Assert.False(vm.IsLeftPanelVisible);
            Assert.Equal(PanelKind.Comments, vm.ActiveLeftPanel);
            Assert.False(vm.IsPropertiesPanelVisible);
            // 다른 설정이 지워지지 않는다
            store.AddRecent("C:\\a.pdf");
            Assert.Equal(false, store.Load().LeftPanelVisible);
            // 알 수 없는 패널 이름·값은 무시
            var vm2 = new MainWindowViewModel();
            vm2.ApplyPanelState(AppSettings.Default with { LeftPanelKind = "Nope", LeftPanelVisible = null });
            Assert.True(vm2.IsLeftPanelVisible);
            Assert.Equal(PanelKind.Thumbnails, vm2.ActiveLeftPanel);
        }
        finally { try { File.Delete(file); } catch { } }
    }

    // ---- 손바닥 ----

    [Fact]
    public void HandIcon_Exists_AndIsOwnDesign()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PdfEditor.App", "Resources", "Icons.axaml"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(xaml, "x:Key=\"Icon\\.hand\""));
        Assert.Contains("x:Key=\"Icon.hand\">F1 M7 18L5.2 14.6", xaml);        // 자작 손 윤곽(HandCursors.OpenPath와 같다)
        Assert.Contains("F1 M7 18L5.2 14.6L2.9 11.2", HandCursors.OpenPath);
        Assert.Equal("hand", new MainWindowViewModel().Commands.Find("tool.hand")!.IconKey);
    }

    [Fact]
    public void Hand_Cursor_GrabGrabbing()
    {
        var (open, grab, custom) = HeadlessUi.Run(() => (HandCursors.Open, HandCursors.Grab, HandCursors.IsCustom));
        Assert.True(custom, "자작 비트맵 커서가 만들어져야 한다(표준 손 커서로 되돌아가면 실패)");
        Assert.NotSame(open, grab);
    }

    // ---- 홈 탭 ----

    [Fact]
    public void HomeTab_HasPencilLineEllipseAreaHighlight_SameCommandsAsAnnotateTab()
    {
        var vm = new MainWindowViewModel();
        var home = vm.Ribbon["Home"].SelectMany(g => g.Items).ToList();
        var annotate = vm.Ribbon["Annotate"].SelectMany(g => g.Items).ToList();
        foreach (var (id, tool) in new[] { ("annot.pencil", ToolKind.Pencil), ("annot.line", ToolKind.Line), ("annot.ellipse", ToolKind.Ellipse), ("annot.hlArea", ToolKind.HighlightArea) })
        {
            var h = home.Single(i => i.CommandId == id);
            Assert.Equal(tool, h.Tool);
            Assert.Contains(annotate, i => i.CommandId == id);
        }
    }

    [Fact]
    public void HomeTab_ToolButton_ActivatesTool()
    {
        var vm = new MainWindowViewModel();
        var d = new DocumentTabViewModel("a.pdf");
        vm.Documents.Add(d);
        vm.ActiveDocument = d;
        vm.SelectedRibbonTab = "Home";
        d.Tool = ToolKind.HighlightArea;
        Assert.Equal([ToolKind.HighlightArea], vm.CurrentRibbonGroups.SelectMany(g => g.Items).Where(b => b.IsChecked).Select(b => b.Tool!.Value));
        d.Tool = ToolKind.Ellipse;
        Assert.Equal([ToolKind.Ellipse], vm.CurrentRibbonGroups.SelectMany(g => g.Items).Where(b => b.IsChecked).Select(b => b.Tool!.Value));
    }

    [Fact]
    public void Ribbon_HomeTab_NoOverflowAt1100()
    {
        var vm = new MainWindowViewModel();
        var d = new DocumentTabViewModel("a.pdf");
        vm.Documents.Add(d);
        vm.ActiveDocument = d;
        vm.SelectedRibbonTab = "Home";
        double width = HeadlessUi.Run(() =>
        {
            var app = Application.Current!;
            if (!app.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>().Any()) app.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
            var baseUri = new Uri("avares://SedolPDF/");
            if (!app.Resources.MergedDictionaries.OfType<ResourceInclude>().Any(r => r.Source?.AbsolutePath.EndsWith("Icons.axaml") == true))
                app.Resources.MergedDictionaries.Add(new ResourceInclude(baseUri) { Source = new Uri("avares://SedolPDF/Resources/Icons.axaml") });
            if (!app.Styles.OfType<StyleInclude>().Any(r => r.Source?.AbsolutePath.EndsWith("RibbonStyles.axaml") == true))
                app.Styles.Add(new StyleInclude(baseUri) { Source = new Uri("avares://SedolPDF/Shell/Ribbon/RibbonStyles.axaml") });
            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            foreach (var g in vm.CurrentRibbonGroups)
            {
                var grp = new RibbonGroup { Header = g.Name, IsCompact = g.Compact, ItemsSource = g.Items };
                grp.ItemTemplate = new FuncDataTemplate<RibbonButtonVm>((b, _) => new RibbonButton
                {
                    Label = b.DisplayLabel, IconKey = b.Command.IconKey, IsLarge = b.IsLarge, IsChecked = b.IsChecked,
                });
                panel.Children.Add(grp);
            }
            var w = new Window { Width = 1100, Height = 300, Content = panel };
            w.Show();
            w.UpdateLayout();
            panel.Measure(new Size(double.PositiveInfinity, 200));
            double desired = panel.DesiredSize.Width;
            Detail = string.Join(", ", panel.Children.OfType<RibbonGroup>().Select(c => $"{c.Header}={c.DesiredSize.Width:0}"));
            w.Close();
            return desired;
        });
        // 창 1100 DIP: 바깥 여백 8×2 + 리본 카드 안쪽 여백 4×2 + 테두리 2 를 뺀 폭 안에 들어와야 한다(가로 스크롤 없음)
        Assert.True(width <= 1100 - 16 - 8 - 2, $"홈 탭 리본 폭 {width:0} DIP가 1074를 넘는다 ({Detail})");
    }

    private static string Detail = "";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "PdfEditor.App", "Shell", "MainWindow.axaml"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("repo root");
    }
}
