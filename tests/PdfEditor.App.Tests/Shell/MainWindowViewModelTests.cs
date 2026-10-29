using PdfEditor.App.Resources;
using PdfEditor.App.Documents;
using PdfEditor.App.Shell;
using PdfEditor.App.Shell.Ribbon;

namespace PdfEditor.App.Tests.Shell;

public class MainWindowViewModelTests
{
    [Fact]
    public void ViewModel_Defaults()
    {
        var vm = new MainWindowViewModel();
        Assert.Equal("Home", vm.SelectedRibbonTab);
        Assert.Equal(PanelKind.Thumbnails, vm.ActiveLeftPanel);
        Assert.True(vm.IsPropertiesPanelVisible);
        Assert.True(vm.IsLeftPanelVisible);
        Assert.False(vm.IsFullScreen);
        Assert.Empty(vm.Documents);
        Assert.Null(vm.ActiveDocument);
        Assert.Empty(vm.QuickCommandResults);
    }

    [Fact]
    public void ViewModel_AllRibbonCommandsRegistered()
    {
        var vm = new MainWindowViewModel();
        var ids = vm.Ribbon.Values.SelectMany(gs => gs).SelectMany(g => g.Items).Select(i => i.CommandId)
            .Concat(vm.FileMenuCommandIds).Concat(vm.QuickAccessCommandIds).Distinct().ToList();
        Assert.NotEmpty(ids);
        var missing = ids.Where(id => vm.Commands.Find(id) is null).ToList();
        Assert.True(missing.Count == 0, "Not registered: " + string.Join(", ", missing));
        Assert.Equal(["Home", "Annotate", "Edit", "Page", "View", "Help"], vm.Ribbon.Keys);
    }

    [Fact]
    public void ViewModel_RibbonTabSelection_ChangesGroups()
    {
        var vm = new MainWindowViewModel();
        var home = vm.CurrentRibbonGroups;
        vm.SelectedRibbonTab = "Page";
        Assert.NotSame(home, vm.CurrentRibbonGroups);
        Assert.True(vm.RibbonTabs.Single(t => t.Id == "Page").IsSelected);
        Assert.False(vm.RibbonTabs.Single(t => t.Id == "Home").IsSelected);
    }

    [Fact]
    public void ViewModel_AnnotateAndEdit_AreDisabledWithoutDocument_EditHasTooltip()
    {
        var vm = new MainWindowViewModel();
        foreach (var tab in new[] { "Annotate", "Edit" })
            foreach (var g in vm.Ribbon[tab].SelectMany(x => x.Items).Where(i => i.Tool is null))
            {
                Assert.False(vm.Commands.Find(g.CommandId)!.Command.CanExecute(null), g.CommandId);
                if (tab == "Edit") Assert.False(string.IsNullOrEmpty(g.DisabledTip), g.CommandId);
            }
        foreach (var g in vm.Ribbon["Annotate"].SelectMany(x => x.Items))
            Assert.False(vm.Commands.Find(g.CommandId)!.Command.CanExecute(null), g.CommandId);
    }

    [Fact]
    public void AnnotateTab_AllToolsVisible_NoMoreMenu()
    {
        // 2026-10-20 사용자 요청(PDF-XChange 주석 탭): "더보기" 없이 모든 도구가 보인다
        var vm = new MainWindowViewModel();
        var groups = vm.Ribbon["Annotate"];
        var items = groups.SelectMany(g => g.Items).ToList();
        Assert.DoesNotContain(items, i => i.MoreItems is not null);
        // 다른 프로그램과 같은 배치를 피한 자체 구성(2026-10-20 사용자 결정): 묶음마다 대표 도구 하나가 큰 단추
        Assert.Equal([Strings.Group_Select, Strings.Group_Writing, Strings.Group_Emphasis, Strings.Group_Draw, Strings.Group_Shapes,
                      Strings.Group_Marks, Strings.Group_AnnotManage], groups.Select(g => g.Name));
        Assert.All(groups, g => Assert.True(g.Compact));
        Assert.Equal(["tool.hand", "annot.textbox", "annot.hlText", "annot.pencil", "annot.stamp", "annot.commentList"],
            items.Where(i => i.Large).Select(i => i.CommandId));
        Assert.Equal([Strings.Group_Shapes], groups.Where(g => g.IconOnly).Select(g => g.Name));
        // 만들기 도구는 한 번씩, 쪽 객체 도구(M4: 편집 탭)는 주석 탭에 없다
        var all = items.Where(i => i.Tool is not null).Select(i => i.Tool!.Value).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        var objectTools = new[] { ToolKind.EditObject, ToolKind.EditText, ToolKind.AddText, ToolKind.AddImage, ToolKind.AddShape, ToolKind.AddEllipse, ToolKind.AddLine, ToolKind.Snapshot };
        Assert.Equal(Enum.GetValues<ToolKind>().Except(objectTools).Order(), all.Order());
        Assert.Equal(["annot.commentList", "annot.summary", "annot.importXfdf", "annot.exportXfdf"], groups[^1].Items.Select(i => i.CommandId));
    }

    [Fact]
    public void AnnotateTab_IconOnlyButtons_ShowNameAsTooltip()
    {
        var vm = new MainWindowViewModel();
        vm.SelectedRibbonTab = "Annotate";
        var shapes = vm.CurrentRibbonGroups.Single(g => g.Name == Strings.Group_Shapes).Items;
        Assert.Equal(7, shapes.Count);
        foreach (var b in shapes)
        {
            Assert.True(b.IconOnly);
            Assert.Equal("", b.DisplayLabel);
            Assert.Equal(b.Label, b.Tip);
        }
    }
    [Fact]
    public void HomeTab_SnapshotAndQuickAnnotations()
    {
        // 2026-10-20 사용자 요청: 홈 탭에 스냅숏은 반드시, 자주 쓰는 주석·편집 도구도 둔다
        var vm = new MainWindowViewModel();
        var home = vm.Ribbon["Home"];
        Assert.Equal([Strings.Group_Select, Strings.Group_Capture, Strings.Group_Zoom, Strings.Group_Rotate, Strings.Group_Find,
                      Strings.Group_EditTools, Strings.Group_QuickAnnot, Strings.Group_Draw], home.Select(g => g.Name));
        var snap = home.Single(g => g.Name == Strings.Group_Capture).Items.Single();
        Assert.Equal(("tool.snapshot", (ToolKind?)ToolKind.Snapshot), (snap.CommandId, snap.Tool));
        Assert.Equal(["annot.stamp"],
            home.Single(g => g.Name == Strings.Group_QuickAnnot).Items.Select(i => i.CommandId));
        Assert.Equal(["annot.typewriter", "annot.pencil", "annot.line", "annot.arrow", "annot.rect", "annot.ellipse", "annot.hlText", "annot.hlArea", "annot.underline", "annot.sticky"],
            home.Single(g => g.Name == Strings.Group_Draw).Items.Select(i => i.CommandId));

        var d = new DocumentTabViewModel("a.pdf");
        vm.Documents.Add(d);
        vm.ActiveDocument = d;
        vm.SelectedRibbonTab = "Home";
        d.Tool = ToolKind.Snapshot;   // 명령은 세션이 있어야 실행된다(여기서는 단추 표시만 본다)
        Assert.True(vm.CurrentRibbonGroups.SelectMany(g => g.Items).Single(b => b.Command.Id == "tool.snapshot").IsChecked);
    }

    [Fact]
    public void ToolButton_IsChecked_FollowsActiveTool()
    {
        var vm = new MainWindowViewModel();
        var d = new DocumentTabViewModel("a.pdf");
        vm.Documents.Add(d);
        vm.ActiveDocument = d;
        vm.SelectedRibbonTab = "Annotate";
        var buttons = vm.CurrentRibbonGroups.SelectMany(g => g.Items).ToList();
        Assert.Equal(28, buttons.Count);   // 만들기 도구 24 + 주석 관리 4
        Assert.Equal([ToolKind.Hand], buttons.Where(b => b.IsChecked).Select(b => b.Tool!.Value));
        d.Tool = ToolKind.Pencil;
        Assert.Equal([ToolKind.Pencil], buttons.Where(b => b.IsChecked).Select(b => b.Tool!.Value));
        d.Tool = ToolKind.Line;
        Assert.Equal([ToolKind.Line], buttons.Where(b => b.IsChecked).Select(b => b.Tool!.Value));
        vm.ActiveDocument = null;
        Assert.DoesNotContain(buttons, b => b.IsChecked);
    }

    [Fact]
    public void ViewModel_PanelToggles_AndFullScreen()
    {
        var vm = new MainWindowViewModel();
        vm.Commands.Find("view.leftPanel")!.Command.Execute(null);
        Assert.False(vm.IsLeftPanelVisible);
        vm.Commands.Find("view.properties")!.Command.Execute(null);
        Assert.False(vm.IsPropertiesPanelVisible);
        vm.Commands.Find("view.fullscreen")!.Command.Execute(null);
        Assert.True(vm.IsFullScreen);
        vm.SetPanelCommand.Execute(PanelKind.Search);
        Assert.Equal(PanelKind.Search, vm.ActiveLeftPanel);
        Assert.True(vm.IsLeftPanelVisible);
    }

    [Fact]
    public void ViewModel_QuickCommand_SearchesRegistry()
    {
        var vm = new MainWindowViewModel();
        vm.QuickCommandText = "페이지";
        Assert.NotEmpty(vm.QuickCommandResults);
        vm.QuickCommandText = "";
        Assert.Empty(vm.QuickCommandResults);
    }

    [Fact]
    public void ViewModel_ZoomSlider_LogScale_10To1600()
    {
        var vm = new MainWindowViewModel();
        vm.ZoomSliderValue = 0; Assert.Equal(10, vm.ZoomPercent, 6);
        vm.ZoomSliderValue = 1; Assert.Equal(1600, vm.ZoomPercent, 6);
        vm.ZoomPercent = 100; Assert.Equal(Math.Log(10) / Math.Log(160), vm.ZoomSliderValue, 6);
    }

    [Fact]
    public void ViewModel_ActiveDocument_DrivesStatus()
    {
        var vm = new MainWindowViewModel();
        var d = new DocumentTabViewModel("a.pdf") { PageCount = 12, CurrentPageIndex = 2, PageWidthMm = 420, PageHeightMm = 297 };
        vm.Documents.Add(d);
        vm.ActiveDocument = d;
        Assert.Equal(3, vm.CurrentPageNumber);
        Assert.Equal(12, vm.PageCount);
        Assert.Contains("420.0", vm.StatusSizeText);
        vm.CloseDocumentCommand.Execute(d);
        Assert.Empty(vm.Documents);
        Assert.Null(vm.ActiveDocument);
    }

    [Fact]
    public void FormatTab_HiddenWithoutDocument()
    {
        var vm = new MainWindowViewModel();
        Assert.False(vm.IsFormatTabVisible);
        Assert.Equal("", vm.FormatTabSubtitle);
        vm.SelectRibbonTabCommand.Execute(MainWindowViewModel.FormatTabId);   // 보이지 않는 탭은 고를 수 없다
        Assert.Equal("Home", vm.SelectedRibbonTab);
        Assert.False(vm.IsFormatTabSelected);
    }
}

[Collection("TabSettings")]
public class MainWindowFormatTabTests : PdfEditor.App.Tests.Panels.PanelTestBase
{
    [Fact]
    public async Task FormatTab_VisibleForToolOrSelection()
    {
        string p = Path.Combine(Dir, "f.pdf");
        PdfEditor.TestFixtures.TestPdfFactory.CreateLabeled(p, [(400, 600)]);
        var vm = new MainWindowViewModel();
        var tab = await OpenTab(p);
        vm.ShowSession(tab.Session!);
        var active = vm.ActiveDocument!;
        Assert.NotNull(active.Styles);
        Assert.Same(active.Properties!.Annotation, vm.ActiveAnnotationProperties);   // 속성 패널과 같은 모델
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.False(vm.IsFormatTabVisible);   // 손 도구, 선택 없음
        active.Tool = ToolKind.Cloud;
        Assert.True(vm.IsFormatTabVisible);
        Assert.Equal(string.Format(PdfEditor.App.Resources.Strings.Format_TargetTool, PdfEditor.App.Resources.Strings.AnnotKind_Cloud), vm.FormatTabSubtitle);
        Assert.Contains(nameof(MainWindowViewModel.IsFormatTabVisible), changed);

        vm.SelectedRibbonTab = MainWindowViewModel.FormatTabId;
        Assert.True(vm.IsFormatTabSelected);
        Assert.Empty(vm.CurrentRibbonGroups);

        active.Tool = ToolKind.Eraser;   // 만들기 도구가 아님 → 사라지고 주석 탭으로 돌아간다
        Assert.False(vm.IsFormatTabVisible);
        Assert.Equal("Annotate", vm.SelectedRibbonTab);

        // 선택이 있어도 나타난다
        var page = active.Session!.Pages[0].Id;
        var a = new PdfEditor.Document.Annotations.Annotation
        {
            Id = active.Session.Annotations.NewId(), Kind = PdfEditor.Document.Annotations.AnnotKind.Square,
            Rect = new Pdf.Native.PdfRectF(10, 10, 80, 60), Color = PdfEditor.Document.Annotations.PdfColor.FromHex("#E5372D"),
            BorderWidth = 1f, Opacity = 1f, Flags = Pdf.Native.PdfAnnotFlags.Print,
        };
        await active.RunAnnotationCommandAsync(PdfEditor.Document.Annotations.ChangeAnnotationsCommand.Add(page, a));
        active.Tool = ToolKind.AnnotSelect;
        Assert.False(vm.IsFormatTabVisible);
        active.Selection.Set(page, [a.Id]);
        Assert.True(vm.IsFormatTabVisible);
        Assert.Equal(string.Format(PdfEditor.App.Resources.Strings.Format_TargetSelection, PdfEditor.App.Resources.Strings.AnnotKind_Square, 1), vm.FormatTabSubtitle);
        active.Selection.Clear();
        Assert.False(vm.IsFormatTabVisible);
        active.Detach();
    }
}
