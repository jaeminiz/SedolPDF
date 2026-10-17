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
