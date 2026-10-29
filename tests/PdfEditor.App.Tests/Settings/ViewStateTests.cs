using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Settings;
using PdfEditor.App.Shell;
using PdfEditor.App.Tests.Documents;
using PdfEditor.App.Tests.Panels;
using PdfEditor.App.Viewer;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Settings;

/// <summary>0.4.0 Task 11: 파일마다 마지막 보던 쪽·배율을 기억하고, 같은 파일을 다시 열면 되살린다. 도구별 마지막 모양 기억 점검.</summary>
[Collection("TabSettings")]
public class ViewStateTests : PanelTestBase, IDisposable
{
    private readonly SettingsStore _store;

    public ViewStateTests()
    {
        Directory.CreateDirectory(Dir);
        _store = new SettingsStore(Path.Combine(Dir, "settings.json"));
        DocumentTabViewModel.Settings = _store;
    }

    void IDisposable.Dispose() => DocumentTabViewModel.Settings = null;

    private MainWindowViewModel NewVm()
    {
        var prompts = new FakePrompts();
        var vm = new MainWindowViewModel();
        vm.AttachWorkflow(new FileWorkflow(Thread, new SaveService([]), _store, prompts), prompts, a => a());
        return vm;
    }

    private string FivePages()
    {
        string p = Path.Combine(Dir, "five.pdf");
        TestPdfFactory.CreateLabeled(p, [(400, 600), (400, 600), (400, 600), (400, 600), (400, 600)]);
        return p;
    }

    // ---- 규칙(순수 코드) ----

    [Fact]
    public void Upsert_PutsNewestFirst_ReplacesSamePath_AndKeepsAtMost50()
    {
        IReadOnlyList<ViewStateEntry>? list = null;
        for (int i = 0; i < 60; i++) list = ViewStates.Upsert(list, new($@"C:\a\f{i}.pdf", 1, 1.0, "Continuous", 5));
        Assert.Equal(50, list!.Count);
        Assert.Equal(@"C:\a\f59.pdf", list[0].Path);
        list = ViewStates.Upsert(list, new(@"C:\A\F30.PDF", 2, 2.0, "SinglePage", 5));   // 같은 경로(대소문자 무시)는 하나만
        Assert.Equal(50, list.Count);
        Assert.Equal(2, list[0].Page);
        Assert.Single(list, e => string.Equals(e.Path, @"C:\a\f30.pdf", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(2, 1.5, "Continuous", 5, true)]
    [InlineData(5, 1.5, "Continuous", 5, false)]      // 쪽 번호가 범위 밖
    [InlineData(-1, 1.5, "Continuous", 5, false)]
    [InlineData(2, 99.0, "Continuous", 5, false)]     // 배율이 범위 밖
    [InlineData(2, 0.01, "Continuous", 5, false)]
    [InlineData(2, double.NaN, "Continuous", 5, false)]
    [InlineData(2, 1.5, "Bogus", 5, false)]           // 알 수 없는 보기 방식
    [InlineData(2, 1.5, "SinglePage", 3, false)]      // 저장 때와 쪽 수가 다르면 파일이 바뀐 것으로 보고 무시
    public void Validate_AcceptsOnlySaneValues(int page, double scale, string mode, int savedCount, bool ok)
    {
        var e = new ViewStateEntry(@"C:\x.pdf", page, scale, mode, savedCount);
        Assert.Equal(ok, ViewStates.Validate(e, 5) is not null);
    }

    [Fact]
    public void Validate_NullOrEmptyDocument_IsIgnored()
    {
        Assert.Null(ViewStates.Validate(null, 5));
        Assert.Null(ViewStates.Validate(new(@"C:\x.pdf", 0, 1.0, "Continuous", 0), 0));
    }

    [Fact]
    public void Settings_SaveAndLoad_RoundTrip_AndOldFilesHaveNone()
    {
        Assert.Null(_store.Load().ViewStates);
        _store.Update(s => s with { ViewStates = ViewStates.Upsert(s.ViewStates, new(@"C:\x.pdf", 3, 1.25, "SinglePage", 9)) });
        var back = new SettingsStore(Path.Combine(Dir, "settings.json")).Load().ViewStates!;
        Assert.Equal(new ViewStateEntry(@"C:\x.pdf", 3, 1.25, "SinglePage", 9), back[0]);
        // 손상된 항목(경로·방식이 null)은 읽을 때 걸러진다
        File.WriteAllText(Path.Combine(Dir, "settings.json"),
            "{\"RecentFiles\":[],\"ViewStates\":[null,{\"Path\":\"\",\"Page\":1,\"Scale\":1,\"Mode\":\"Continuous\",\"PageCount\":2},{\"Path\":\"C:\\\\y.pdf\",\"Page\":1,\"Scale\":1,\"Mode\":\"Continuous\",\"PageCount\":2}]}");
        Assert.Single(_store.Load().ViewStates!);
    }

    // ---- 탭·열기 경로 ----

    [Fact]
    public async Task ViewState_Restored_OnReopen()
    {
        string p = FivePages();
        var vm = NewVm();
        await vm.OpenAsync(p);
        var tab = vm.ActiveDocument!;
        Assert.Equal(0, tab.CurrentPageIndex);
        tab.CurrentPageIndex = 3;
        tab.Scale = 1.5;
        tab.ViewMode = ViewMode.SinglePage;

        Assert.True(await vm.CloseDocumentAsync(tab));
        var saved = ViewStates.Find(_store.Load().ViewStates, Path.GetFullPath(p));
        Assert.NotNull(saved);

        await vm.OpenAsync(p);
        var again = vm.ActiveDocument!;
        Assert.Equal(3, again.CurrentPageIndex);
        Assert.Equal(1.5, again.Scale, 6);
        Assert.Equal(ViewMode.SinglePage, again.ViewMode);
    }

    [Fact]
    public async Task ViewState_OutOfRange_Ignored()
    {
        string p = FivePages();
        string full = Path.GetFullPath(p);
        // 쪽 수가 달라졌거나 값이 범위 밖인 옛 기록은 처음 열 때처럼 보인다(오류 없음)
        foreach (var bad in new[]
        {
            new ViewStateEntry(full, 3, 1.5, "Continuous", 2),
            new ViewStateEntry(full, 99, 1.5, "Continuous", 5),
            new ViewStateEntry(full, 2, 1000.0, "Continuous", 5),
            new ViewStateEntry(full, 2, 1.5, "Nonsense", 5),
        })
        {
            _store.Update(s => s with { ViewStates = [bad] });
            var vm = NewVm();
            await vm.OpenAsync(p);
            var tab = vm.ActiveDocument!;
            Assert.Equal(0, tab.CurrentPageIndex);
            Assert.Equal(1.0, tab.Scale, 6);
            Assert.Equal(ViewMode.Continuous, tab.ViewMode);
            await vm.CloseDocumentAsync(tab);
        }
    }

    [Fact]
    public async Task ViewState_UnsavedOrUntitledTab_IsNotRecorded_AndNoStoreIsFine()
    {
        var empty = new DocumentTabViewModel("제목 없음");
        Assert.Null(empty.CaptureViewState());
        Assert.False(empty.ApplyViewState(new ViewStateEntry(@"C:\x.pdf", 0, 1.0, "Continuous", 1)));

        // 설정 저장소가 없어도 닫기·열기가 오류 없이 된다
        DocumentTabViewModel.Settings = null;
        string p = FivePages();
        var prompts = new FakePrompts();
        var vm = new MainWindowViewModel();
        vm.AttachWorkflow(new FileWorkflow(Thread, new SaveService([]), _store, prompts), prompts, a => a());
        await vm.OpenAsync(p);
        Assert.True(await vm.CloseDocumentAsync(vm.ActiveDocument!));
        Assert.Null(_store.Load().ViewStates);
    }

    // ---- 도구별 마지막 모양 기억 점검 ----

    // AnnotationPropertiesViewModel.IsCreateTool(속성 패널에 도구 기본 모양이 나오는 도구)과 같은 목록 — 도구가 늘면 여기도 늘린다.
    private static readonly ToolKind[] CreateTools =
    [
        ToolKind.Pencil, ToolKind.HighlightText, ToolKind.HighlightArea, ToolKind.Underline, ToolKind.StrikeOut, ToolKind.StickyNote,
        ToolKind.Rectangle, ToolKind.Ellipse, ToolKind.Line, ToolKind.Arrow, ToolKind.PolyLine, ToolKind.Polygon, ToolKind.Cloud,
        ToolKind.TextBox, ToolKind.Typewriter, ToolKind.Callout, ToolKind.InsertText, ToolKind.ReplaceText, ToolKind.Stamp,
    ];

    [Fact]
    public void ToolStyles_AllAnnotTools_HaveRemembered()
    {
        // 속성 패널이 "새로 만들 주석의 기본 모양"을 보여 주는 모든 만들기 도구: 저장한 모양이 그대로 다시 나온다.
        var missing = new List<ToolKind>();
        foreach (var tool in CreateTools)
        {
            var want = new ToolStyle(new PdfEditor.Document.Annotations.PdfColor(10, 20, 30), null, 0.6f, 3f);
            DocumentTabViewModel.SaveToolStyle(tool, want);
            var got = DocumentTabViewModel.StyleFor(tool);
            if (got.Color != want.Color || Math.Abs(got.Opacity - want.Opacity) > 0.001f || Math.Abs(got.Width - want.Width) > 0.001f) missing.Add(tool);
        }
        Assert.Empty(missing);
        Assert.Equal(19, CreateTools.Length);
    }
}
