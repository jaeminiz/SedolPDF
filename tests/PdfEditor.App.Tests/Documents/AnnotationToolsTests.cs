using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Shell;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Documents;

public class AnnotationToolsTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-at-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly List<DocumentSession> _sessions = [];

    public Task InitializeAsync() { Directory.CreateDirectory(_dir); return Task.CompletedTask; }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions) await s.DisposeAsync();
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<DocumentTabViewModel> OpenAsync(Action<string> create)
    {
        string p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        create(p);
        var s = await DocumentSession.OpenAsync(_t, p, null);
        _sessions.Add(s);
        return new DocumentTabViewModel(s, "a.pdf", null);
    }

    [Fact]
    public async Task Tab_WiresToolControllerProperties_ForPatchTools()
    {
        var tab = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var t = tab.Tools;
        Assert.True(t.CanUsePatchFeatures());   // 암호 없는 문서
        Assert.NotNull(t.FitTypewriter);
        Assert.NotNull(t.Assets);
        Assert.NotNull(t.CharBoundaryAt);
        Assert.NotNull(t.NewName);
        Assert.NotEqual(t.NewName(), t.NewName());
        // 스탬프 표시 문구는 리소스(한국어), 기본 스탬프는 문구를 Text에 담는다
        Assert.Equal(Strings.Stamp_Approved, t.StampLabel(DocumentTabViewModel.BuiltinStamp("Approved")));
        Assert.Equal(Strings.Stamp_Approved, t.LastStamp.Text);
        // 앱이 연결한 공유 보관소가 그대로 쓰인다
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        var services = AppearanceServices.Create(dir!.FullName);
        DocumentTabViewModel.Appearance = services;
        try
        {
            Assert.Same(services.Assets, t.Assets!());
            var fitted = t.FitTypewriter!(new Annotation { Kind = AnnotKind.FreeText, Intent = FreeTextIntent.Typewriter,
                Rect = new PdfRectF(10, 90, 30, 100), Contents = "wide wide wide text", FontSizePt = 12f });
            Assert.True(fitted.Rect.Width > 30f);
        }
        finally
        {
            DocumentTabViewModel.Appearance = null;
            PdfEditor.App.Viewer.Annotations.AppearanceGeometry.TextFont = null;
            PdfEditor.App.Viewer.Annotations.AppearanceGeometry.ImageResolver = null;
            PdfEditor.App.Viewer.Annotations.AppearanceGeometry.Assets = AnnotationAssets.Empty;
        }
    }

    [Fact]
    public async Task ToolChange_AwayFromFileAttach_ClearsPendingAttach()
    {
        var tab = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        tab.Tool = ToolKind.FileAttach;
        var page = tab.Session!.Pages[0].Id;
        tab.Tools.Begin(ToolKind.FileAttach, ToolDefaults.For(ToolKind.FileAttach), page, new PdfPointF(50, 50), 1f);
        Assert.Null(tab.Tools.End(new PdfPointF(50, 50)));
        Assert.NotNull(tab.Tools.PendingAttach);
        tab.Tool = ToolKind.Hand;
        Assert.Null(tab.Tools.PendingAttach);
    }

    [Fact]
    public async Task RestrictedDoc_AnnotationToolsDisabled()
    {
        var tab = await OpenAsync(p => AnnotatedPdfFactory.CreateOwnerRestrictedWithAnnotations(p, allowAnnotations: false));
        Assert.False(tab.CanAnnotate);
        Assert.Equal(Strings.Msg_AnnotationsRestricted, tab.AnnotationNotice);
        var vm = new MainWindowViewModel();
        vm.Documents.Add(tab);
        vm.ActiveDocument = tab;
        foreach (var id in new[] { "annot.select", "annot.sticky", "annot.hlText", "annot.hlArea", "annot.underline", "annot.strike",
                                   "annot.rect", "annot.ellipse", "annot.pencil", "annot.eraser" })
            Assert.False(vm.Commands.Find(id)!.Command.CanExecute(null), id);
        Assert.True(vm.Commands.Find("tool.hand")!.Command.CanExecute(null));
        // 명령을 직접 실행해도 도구가 바뀌지 않는다.
        vm.Commands.Find("annot.pencil")!.Command.Execute(null);
        Assert.Equal(ToolKind.Hand, tab.Tool);
        tab.Detach();
    }

    [Fact]
    public async Task EncryptedDoc_PatchFeaturesUnavailable_NoticeShown()
    {
        string p = Path.Combine(Path.GetTempPath(), "pdfeditor-enc-" + Guid.NewGuid().ToString("N") + ".pdf");
        TestPdfFactory.CreateEncrypted(p, 1, 400, 600, "secret");
        try
        {
            var s = await DocumentSession.OpenAsync(_t, p, "secret");
            _sessions.Add(s);
            var tab = new DocumentTabViewModel(s, "a.pdf", null);
            await s.AnnotationsReady;
            Assert.False(tab.CanUsePatchFeatures);
            Assert.True(tab.CanAnnotate);
            Assert.Equal(Strings.Msg_PatchFeaturesUnavailableEncrypted, tab.AnnotationNotice);
            Assert.True(tab.HasAnnotationNotice);
            tab.Detach();
        }
        finally { try { File.Delete(p); } catch { } }
    }

    private static readonly string[] PatchToolCommandIds =
    [
        "annot.line", "annot.arrow", "annot.polyline", "annot.polygon", "annot.cloud", "annot.textbox", "annot.typewriter",
        "annot.callout", "annot.insertText", "annot.replaceText", "annot.stamp", "annot.attach",
    ];

    private async Task<DocumentTabViewModel> OpenEncryptedPatchKindsAsync(string? password = "secret")
    {
        string p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        AnnotatedPdfFactory.CreatePatchKinds(p, password);
        var s = await DocumentSession.OpenAsync(_t, p, password);
        _sessions.Add(s);
        var tab = new DocumentTabViewModel(s, "a.pdf", null);
        await s.AnnotationsReady;
        return tab;
    }

    [Fact]
    public async Task EncryptedDoc_PatchToolsDisabledWithNotice()
    {
        var tab = await OpenEncryptedPatchKindsAsync();
        Assert.True(tab.CanAnnotate);
        Assert.False(tab.CanUsePatchFeatures);
        var vm = new MainWindowViewModel();
        vm.Documents.Add(tab);
        vm.ActiveDocument = tab;
        vm.SelectedRibbonTab = "Annotate";
        foreach (var id in PatchToolCommandIds)
        {
            var cmd = vm.Commands.Find(id)!.Command;
            Assert.False(cmd.CanExecute(null), id);
            cmd.Execute(null);   // 직접 실행해도 도구가 바뀌지 않는다
            Assert.Equal(ToolKind.Hand, tab.Tool);
        }
        // 보이는 단추와 "더보기" 메뉴 항목 모두 이유 안내 툴팁을 가진다
        var buttons = vm.CurrentRibbonGroups.SelectMany(g => g.Items).SelectMany(b => b.MoreItems is { } m ? m.Prepend(b) : [b])
            .Where(b => b.Tool is { } t && AnnotationToolController.IsPatchTool(t)).ToList();
        Assert.Equal(PatchToolCommandIds.Length, buttons.Select(b => b.Command.Id).Distinct().Count());
        foreach (var b in buttons) Assert.Equal(Strings.Msg_PatchFeaturesUnavailableEncrypted, b.Tip);
        Assert.Equal(Strings.Msg_PatchFeaturesUnavailableEncrypted, tab.AnnotationNotice);
        tab.Detach();
    }

    [Fact]
    public async Task EncryptedDoc_M2ToolsStillEnabled()
    {
        var tab = await OpenEncryptedPatchKindsAsync();
        var vm = new MainWindowViewModel();
        vm.Documents.Add(tab);
        vm.ActiveDocument = tab;
        vm.SelectedRibbonTab = "Annotate";
        string[] m2 = ["annot.select", "annot.sticky", "annot.hlText", "annot.hlArea", "annot.underline", "annot.strike",
                       "annot.rect", "annot.ellipse", "annot.pencil", "annot.eraser"];
        foreach (var id in m2) Assert.True(vm.Commands.Find(id)!.Command.CanExecute(null), id);
        vm.Commands.Find("annot.pencil")!.Command.Execute(null);
        Assert.Equal(ToolKind.Pencil, tab.Tool);
        var buttons = vm.CurrentRibbonGroups.SelectMany(g => g.Items).SelectMany(b => b.MoreItems is { } m ? m.Prepend(b) : [b])
            .Where(b => b.Tool is { } t && t is not ToolKind.Hand and not ToolKind.TextSelect && !AnnotationToolController.IsPatchTool(t)).ToList();
        Assert.NotEmpty(buttons);
        foreach (var b in buttons) Assert.True(b.Tip is null || (b.IconOnly && b.Tip == b.Label), b.Command.Id);   // 그림만 있는 단추는 이름이 툴팁
        tab.Detach();
    }

    [Fact]
    public async Task UnencryptedDoc_PatchToolsEnabled_NoTip()
    {
        var tab = await OpenAsync(p => AnnotatedPdfFactory.CreatePatchKinds(p, null));
        var vm = new MainWindowViewModel();
        vm.Documents.Add(tab);
        vm.ActiveDocument = tab;
        vm.SelectedRibbonTab = "Annotate";
        foreach (var id in PatchToolCommandIds) Assert.True(vm.Commands.Find(id)!.Command.CanExecute(null), id);
        var buttons = vm.CurrentRibbonGroups.SelectMany(g => g.Items).SelectMany(b => b.MoreItems is { } m ? m.Prepend(b) : [b])
            .Where(b => b.Tool is { } t && AnnotationToolController.IsPatchTool(t));
        foreach (var b in buttons) Assert.True(b.Tip is null || (b.IconOnly && b.Tip == b.Label), b.Command.Id);   // 그림만 있는 단추는 이름이 툴팁
        tab.Detach();
    }

    [Fact]
    public async Task EncryptedDoc_OriginalFreeTextReadOnly()
    {
        var open = await OpenAsync(p => AnnotatedPdfFactory.CreatePatchKinds(p, null));
        await open.Session!.AnnotationsReady;
        var openKinds = open.Session.Annotations.Get(open.Session.Pages[0].Id).Items.Select(v => v.Data.Kind).ToList();
        // 암호 없는 문서에서는 모두 편집 가능 (고정물이 의미 있는지 확인)
        Assert.Contains(AnnotKind.FreeText, openKinds);
        Assert.Contains(AnnotKind.Line, openKinds);
        Assert.Contains(AnnotKind.Polygon, openKinds);
        Assert.Contains(AnnotKind.Caret, openKinds);
        Assert.Equal(2, open.Session.Annotations.Get(open.Session.Pages[0].Id).Items.Count(v => v.Data.Kind == AnnotKind.Square));
        open.Detach();

        var tab = await OpenEncryptedPatchKindsAsync();
        var s = tab.Session!;
        var items = s.Annotations.Get(s.Pages[0].Id).Items.Select(v => v.Data).ToList();
        // 편집할 수 있는 것: 일반 Square, Text, Ink. 패치가 필요한 원본(Line·FreeText·Polygon·Caret 묶음·클라우드)은 읽기 전용.
        Assert.DoesNotContain(items, a => a.Kind is AnnotKind.FreeText or AnnotKind.Line or AnnotKind.Polygon or AnnotKind.PolyLine or AnnotKind.Caret);
        Assert.DoesNotContain(items, a => a.CloudIntensity > 0f);
        Assert.DoesNotContain(items, a => a.Kind == AnnotKind.StrikeOut);   // Caret 묶음의 부모도 읽기 전용
        Assert.Single(items, a => a.Kind == AnnotKind.Square);
        Assert.Single(items, a => a.Kind == AnnotKind.Text);
        Assert.Single(items, a => a.Kind == AnnotKind.Ink);
        tab.Detach();
    }

    [Fact]
    public async Task EncryptedDoc_PanelCloudToggle_Disabled()
    {
        var tab = await OpenEncryptedPatchKindsAsync();
        var s = tab.Session!;
        var page = s.Pages[0].Id;
        var square = s.Annotations.Get(page).Items.Select(v => v.Data).Single(a => a.Kind == AnnotKind.Square);
        tab.Tool = ToolKind.AnnotSelect;
        tab.Selection.Set(page, [square.Id]);
        var panel = new PdfEditor.App.Panels.AnnotationPropertiesViewModel(tab);
        panel.Refresh();
        Assert.False(panel.ShowCloud);
        await panel.SetCloudAsync(true);   // 직접 불러도 바뀌지 않는다
        Assert.Equal(0f, s.Annotations.Get(page).Items.Select(v => v.Data).Single(a => a.Id == square.Id).CloudIntensity);
        tab.Detach();
    }

    [Fact]
    public async Task EncryptedDoc_SaveWithPatchKindEntry_ThrowsInsteadOfDropping()
    {
        var tab = await OpenEncryptedPatchKindsAsync();
        var s = tab.Session!;
        var page = s.Pages[0].Id;
        var line = new Annotation
        {
            Id = s.Annotations.NewId(), Kind = AnnotKind.Line, Rect = new PdfRectF(10, 10, 80, 60),
            Vertices = [new PdfPointF(10, 10), new PdfPointF(80, 60)], BorderWidth = 1f, Color = PdfColor.FromHex("#E5372D"),
            Flags = PdfAnnotFlags.Print,
        };
        // 도구가 꺼져 있으므로 정상 경로로는 일어나지 않는다. 명령을 직접 실행해 가드를 시험한다.
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(page, line));
        var save = new PdfEditor.Document.Saving.SaveService([], new NullAppearance());
        string target = Path.Combine(_dir, "out.pdf");
        var ex = await Assert.ThrowsAsync<PdfEditor.Document.Saving.SaveException>(() => save.SaveAsync(s, target));
        Assert.Contains("patch", ex.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(target));
        tab.Detach();
    }

    private sealed class NullAppearance : IAppearanceSource
    {
        public string BuildContent(Annotation a, int pageRotation) => throw new InvalidOperationException("not expected");
        public PatchAppearance BuildPatchAppearance(Annotation a, int pageRotation) => throw new InvalidOperationException("not expected");
    }

    [Fact]
    public async Task AllowedDoc_AnnotationToolsEnabled()
    {
        var tab = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        Assert.True(tab.CanAnnotate);
        Assert.Null(tab.AnnotationNotice);
        var vm = new MainWindowViewModel();
        vm.Documents.Add(tab);
        vm.ActiveDocument = tab;
        var cmd = vm.Commands.Find("annot.pencil")!.Command;
        Assert.True(cmd.CanExecute(null));
        cmd.Execute(null);
        Assert.Equal(ToolKind.Pencil, tab.Tool);
        tab.Detach();
    }

    [Fact]
    public async Task RunAnnotationCommand_WaitsForAnnotationsReady()
    {
        var tab = await OpenAsync(AnnotatedPdfFactory.CreateMixed);
        var s = tab.Session!;
        var page = s.Pages[0].Id;
        var a = new Annotation
        {
            Id = s.Annotations.NewId(), Kind = AnnotKind.Square, Rect = new PdfRectF(10, 10, 60, 60),
            BorderWidth = 1f, Color = PdfColor.FromHex("#E5372D"), Flags = PdfAnnotFlags.Print,
        };
        await tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Add(page, a));
        Assert.True(s.AnnotationsReady.IsCompleted);
        var items = s.Annotations.Get(page).Items;
        Assert.Contains(items, v => v.Data.Id == a.Id);
        Assert.True(items.Length > 1);   // 원본 주석도 이미 붙어 있다
        tab.Detach();
    }

    [Fact]
    public async Task Escape_ReturnsToAnnotSelect_CancelsGesture()
    {
        var tab = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var page = tab.Session!.Pages[0].Id;
        tab.Tool = ToolKind.Rectangle;
        tab.Tools.Begin(ToolKind.Rectangle, ToolDefaults.For(ToolKind.Rectangle), page, new PdfPointF(10, 10), 1f);
        tab.Tools.Move(new PdfPointF(80, 80));
        Assert.NotNull(tab.Tools.Preview);

        tab.EscapePressed();
        Assert.Equal(ToolKind.AnnotSelect, tab.Tool);
        Assert.Null(tab.Tools.Preview);
        Assert.Null(tab.Tools.End(new PdfPointF(90, 90)));   // 진행 중이던 끌기는 취소됨

        // 주석 선택 도구에서 Esc = 선택 해제, 손 도구에서는 아무 일도 없다.
        tab.Selection.Set(page, [new AnnotId(1)]);
        tab.EscapePressed();
        Assert.Empty(tab.Selection.Ids);
        Assert.Equal(ToolKind.AnnotSelect, tab.Tool);
        tab.Tool = ToolKind.Hand;
        tab.EscapePressed();
        Assert.Equal(ToolKind.Hand, tab.Tool);
        tab.Detach();
    }

    [Fact]
    public async Task ToolChange_ClearsSelection_AndSubjectsComeFromResources()
    {
        var tab = await OpenAsync(p => TestPdfFactory.CreateLabeled(p, [(400, 600)]));
        var page = tab.Session!.Pages[0].Id;
        tab.Tool = ToolKind.AnnotSelect;
        tab.Selection.Set(page, [new AnnotId(1)]);
        tab.Tool = ToolKind.Pencil;
        Assert.Empty(tab.Selection.Ids);
        tab.Tools.Begin(ToolKind.Rectangle, ToolDefaults.For(ToolKind.Rectangle), page, new PdfPointF(10, 10), 1f);
        var cmd = tab.Tools.End(new PdfPointF(90, 90));
        Assert.NotNull(cmd);
        await tab.RunAnnotationCommandAsync(cmd!);
        var added = tab.Session.Annotations.Get(page).Items.Single().Data;
        Assert.Equal(Strings.AnnotKind_Square, added.Subject);
        tab.Detach();
    }
}
