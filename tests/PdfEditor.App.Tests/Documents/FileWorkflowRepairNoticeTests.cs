using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Settings;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Documents;

/// <summary>0.10.0 Task 3: 저장하면서 망가진 쪽을 고쳤으면 알리고 "다시 열기"를 받는다.</summary>
public sealed class FileWorkflowRepairNoticeTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-fwrepair-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly FakePrompts _p = new();
    private readonly List<DocumentSession> _sessions = [];

    public Task InitializeAsync() { Directory.CreateDirectory(_dir); return Task.CompletedTask; }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions) await s.DisposeAsync();
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>고쳤다고 거짓으로 보고하는 패처 — 알림 흐름만 본다.</summary>
    private sealed class FakeRepairer(params int[] pages) : IAnnotationPatcher, IContentRepairer
    {
        public void Apply(string pdfPath, string? password, PatchPlan plan, AnnotationAssets assets) { }
        public IReadOnlyList<int> RepairContent(string pdfPath, string? password) => pages;
    }

    private async Task<(FileWorkflow Wf, DocumentSession S)> Setup(params int[] repaired)
    {
        string path = Path.Combine(_dir, "a.pdf");
        TestPdfFactory.CreateBlank(path, 8, 200, 200);
        var wf = new FileWorkflow(_t, new SaveService([], null, new FakeRepairer(repaired)), new SettingsStore(Path.Combine(_dir, "s.json")), _p);
        var s = (await wf.OpenAsync(path))!;
        _sessions.Add(s);
        return (wf, s);
    }

    [Fact]
    public async Task RepairedPages_AsksReopen_WithPageList()
    {
        var (wf, s) = await Setup(6, 3);
        Assert.True(await wf.SaveAsync(s));
        Assert.Equal(["4, 7"], _p.RepairNotices);
    }

    [Fact]
    public async Task ReopenChosen_RaisesReopenRequested()
    {
        var (wf, s) = await Setup(3);
        _p.ReopenAnswer = true;
        DocumentSession? got = null;
        wf.ReopenRequested += x => got = x;
        await wf.SaveAsync(s);
        Assert.Same(s, got);
    }

    [Fact]
    public async Task CloseChosen_DoesNotRaiseReopenRequested()
    {
        var (wf, s) = await Setup(3);
        _p.ReopenAnswer = false;
        bool raised = false;
        wf.ReopenRequested += _ => raised = true;
        await wf.SaveAsync(s);
        Assert.False(raised);
    }

    [Fact]
    public async Task NothingRepaired_NoPrompt()
    {
        var (wf, s) = await Setup();
        await wf.SaveAsync(s);
        Assert.Empty(_p.RepairNotices);
    }

    [Fact]
    public void FormatPageList_OneBased_Ascending()
    {
        Assert.Equal("4", FileWorkflow.FormatPageList([3]));
        Assert.Equal("4, 7", FileWorkflow.FormatPageList([6, 3]));
    }
}
