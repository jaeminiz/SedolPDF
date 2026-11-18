using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Settings;
using PdfEditor.Document;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Documents;

public sealed class FakePrompts : IUserPrompts
{
    public Queue<string?> Passwords { get; } = new();
    public List<bool> PasswordRetryFlags { get; } = [];
    public UnsavedChoice SaveChoice { get; set; } = UnsavedChoice.Cancel;
    public Queue<string?> SavePicks { get; } = new();
    public int SavePickCalls { get; private set; }
    public List<string> SuggestedNames { get; } = [];
    public List<string> Errors { get; } = [];
    public List<string> Infos { get; } = [];

    public Task<string?> AskPasswordAsync(string fileName, bool retry)
    {
        PasswordRetryFlags.Add(retry);
        return Task.FromResult(Passwords.Count > 0 ? Passwords.Dequeue() : null);
    }
    public List<bool> OfferAllFlags { get; } = [];
    public Queue<UnsavedChoice> SaveChoices { get; } = new();
    public Task<UnsavedChoice> AskSaveChangesAsync(string displayName, bool offerAll = false)
    {
        OfferAllFlags.Add(offerAll);
        return Task.FromResult(SaveChoices.Count > 0 ? SaveChoices.Dequeue() : SaveChoice);
    }
    public string? PickedFile { get; set; }
    public IReadOnlyList<string> PickedFiles { get; set; } = [];
    public InsertPagesRequest? InsertRequest { get; set; }
    public MoveTarget? MoveTargetAnswer { get; set; }
    public IReadOnlyList<string>? MergeOrder { get; set; }
    public Task<string?> PickOpenFileAsync() => Task.FromResult(PickedFile);
    public Task<IReadOnlyList<string>> PickOpenFilesAsync() => Task.FromResult(PickedFiles);
    public Task<InsertPagesRequest?> AskInsertPagesAsync(string fileName, int sourcePageCount) => Task.FromResult(InsertRequest);
    public Task<MoveTarget?> AskMoveTargetAsync(int pageCount, int movingCount) => Task.FromResult(MoveTargetAnswer);
    public Task<IReadOnlyList<string>?> AskMergeFilesAsync(IReadOnlyList<string> paths) => Task.FromResult(MergeOrder);
    public Task<string?> PickSaveFileAsync(string suggestedName)
    {
        SavePickCalls++;
        SuggestedNames.Add(suggestedName);
        OnPickSave?.Invoke();
        return Task.FromResult(SavePicks.Count > 0 ? SavePicks.Dequeue() : null);
    }
    /// <summary>저장 위치를 고르는 순간 불린다(진행 중 상태를 시험에서 엿보려고).</summary>
    public Action? OnPickSave { get; set; }
    public string? XfdfPick { get; set; }
    public List<string> XfdfSuggested { get; } = [];
    public Task<string?> PickSaveXfdfAsync(string suggestedName) { XfdfSuggested.Add(suggestedName); return Task.FromResult(XfdfPick); }
    public string? SummaryPick { get; set; }
    public List<string> SummarySuggested { get; } = [];
    public Task<string?> PickSaveSummaryPdfAsync(string suggestedName) { SummarySuggested.Add(suggestedName); return Task.FromResult(SummaryPick); }
    public int ProgressRuns { get; private set; }
    public async Task<bool> RunWithProgressAsync(string title, string progressFormat, int total, Func<IProgress<int>, CancellationToken, Task> work)
    {
        ProgressRuns++;
        try { await work(new Progress<int>(), CancellationToken.None); return true; }
        catch (OperationCanceledException) { return false; }
    }
    public string? XfdfOpenPick { get; set; }
    public int XfdfOpenCalls { get; private set; }
    public List<(string Message, IReadOnlyList<string> Details)> XfdfResults { get; } = [];
    public Task<string?> PickOpenXfdfAsync() { XfdfOpenCalls++; return Task.FromResult(XfdfOpenPick); }
    public Task ShowXfdfImportResultAsync(string message, IReadOnlyList<string> details) { XfdfResults.Add((message, details)); return Task.CompletedTask; }
    public Task ShowErrorAsync(string message) { Errors.Add(message); return Task.CompletedTask; }
    public Task ShowInfoAsync(string message) { Infos.Add(message); return Task.CompletedTask; }
    public List<string> RepairNotices { get; } = [];
    public bool ReopenAnswer { get; set; }
    public Task<bool> AskReopenAfterRepairAsync(string pageList) { RepairNotices.Add(pageList); return Task.FromResult(ReopenAnswer); }
}

public class FileWorkflowTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-fw-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly FakePrompts _p = new();
    private readonly List<DocumentSession> _sessions = [];
    private SettingsStore _settings = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions) await s.DisposeAsync();
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);
    private FileWorkflow Wf(bool preservesEncryption = SavePolicy.PreservesEncryption)
        => new(_t, new SaveService([]), _settings, _p, preservesEncryption);
    private async Task<DocumentSession?> Open(FileWorkflow wf, string path)
    {
        var s = await wf.OpenAsync(path);
        if (s is not null) _sessions.Add(s);
        return s;
    }

    [Fact]
    public async Task IsBusy_TrueDuringSave_FalseAfter()
    {
        string path = P("a.pdf");
        TestPdfFactory.CreateBlank(path, 1, 200, 200);
        var wf = Wf();
        var s = (await Open(wf, path))!;
        bool? during = null;
        _p.OnPickSave = () => during = wf.IsBusy(s);
        _p.SavePicks.Enqueue(P("b.pdf"));
        Assert.False(wf.IsBusy(s));
        Assert.True(await wf.SaveAsAsync(s));
        Assert.True(during);
        Assert.False(wf.IsBusy(s));
    }

    [Fact]
    public async Task MarkBusy_Nested_ClearsOnLastDispose()
    {
        string path = P("a.pdf");
        TestPdfFactory.CreateBlank(path, 1, 200, 200);
        var wf = Wf();
        var s = (await Open(wf, path))!;
        var outer = wf.MarkBusy(s);
        var inner = wf.MarkBusy(s);
        inner.Dispose();
        Assert.True(wf.IsBusy(s));
        inner.Dispose();   // 두 번 풀어도 한 번만 센다
        Assert.True(wf.IsBusy(s));
        outer.Dispose();
        Assert.False(wf.IsBusy(s));
    }

    [Fact]
    public async Task Open_Encrypted_AsksPassword_RetryOnWrong()
    {
        string path = P("enc.pdf");
        TestPdfFactory.CreateEncrypted(path, 2, 200, 200, "secret");
        _p.Passwords.Enqueue("x");
        _p.Passwords.Enqueue("secret");
        var s = await Open(Wf(), path);
        Assert.NotNull(s);
        Assert.Equal(2, s!.Pages.Length);
        Assert.Equal([false, true], _p.PasswordRetryFlags);
    }

    [Fact]
    public async Task Open_Encrypted_Cancel_ReturnsNull()
    {
        string path = P("enc.pdf");
        TestPdfFactory.CreateEncrypted(path, 1, 200, 200, "secret");
        _p.Passwords.Enqueue(null);
        Assert.Null(await Open(Wf(), path));
        Assert.Single(_p.PasswordRetryFlags);
        Assert.Empty(_p.Errors);
    }

    [Fact]
    public async Task Open_SamePathTwice_ReturnsSameSession()
    {
        string path = P("a.pdf");
        TestPdfFactory.CreateBlank(path, 1, 200, 200);
        var wf = Wf();
        var a = await Open(wf, path);
        var b = await wf.OpenAsync(path);
        Assert.Same(a, b);
    }

    [Fact]
    public async Task Open_Repaired_ShowsInfo()
    {
        string path = P("bad.pdf");
        TestPdfFactory.CreateBlank(path, 2, 200, 200);
        TestPdfFactory.CorruptXref(path);
        var s = await Open(Wf(), path);
        Assert.NotNull(s);
        Assert.True(s!.WasRepaired);
        Assert.Equal([Strings.Msg_OpenedRepaired], _p.Infos);
    }

    [Fact]
    public async Task Open_MissingFile_ShowsErrorAndReturnsNull()
    {
        Assert.Null(await Open(Wf(), P("nope.pdf")));
        Assert.Single(_p.Errors);
    }

    [Fact]
    public async Task Open_AddsToRecent()
    {
        string path = P("a.pdf");
        TestPdfFactory.CreateBlank(path, 1, 200, 200);
        await Open(Wf(), path);
        Assert.Equal([Path.GetFullPath(path)], _settings.Load().RecentFiles);
    }

    [Fact]
    public async Task New_UsesNumberedUntitledNames()
    {
        var wf = Wf();
        var a = await wf.NewAsync(); _sessions.Add(a);
        var b = await wf.NewAsync(); _sessions.Add(b);
        Assert.Equal(string.Format(Strings.Doc_Untitled, 1), a.DisplayName);
        Assert.Equal(string.Format(Strings.Doc_Untitled, 2), b.DisplayName);
    }

    [Fact]
    public async Task Save_Untitled_GoesToSaveAs()
    {
        var wf = Wf();
        var s = await wf.NewAsync(); _sessions.Add(s);
        string target = P("out.pdf");
        _p.SavePicks.Enqueue(target);
        Assert.True(await wf.SaveAsync(s));
        Assert.Equal(1, _p.SavePickCalls);
        Assert.True(File.Exists(target));
        Assert.Equal("out.pdf", s.DisplayName);
        Assert.False(s.IsDirty);
    }

    [Fact]
    public async Task SaveAs_Cancel_ReturnsFalse()
    {
        var wf = Wf();
        var s = await wf.NewAsync(); _sessions.Add(s);
        _p.SavePicks.Enqueue(null);
        Assert.False(await wf.SaveAsync(s));
    }

    [Fact]
    public async Task Save_Encrypted_FollowsSavePolicy()
    {
        string path = P("enc.pdf");
        TestPdfFactory.CreateEncrypted(path, 1, 200, 200, "secret");

        // 암호화가 유지되면 같은 경로에 바로 저장한다.
        _p.Passwords.Enqueue("secret");
        var wfKeep = Wf(preservesEncryption: true);
        var keep = await Open(wfKeep, path);
        Assert.True(await wfKeep.SaveAsync(keep!));
        Assert.Equal(0, _p.SavePickCalls);
        Assert.Empty(_p.Infos);

        // 유지되지 않으면 경고 후 다른 이름으로 저장으로 간다.
        string path2 = P("enc2.pdf");
        TestPdfFactory.CreateEncrypted(path2, 1, 200, 200, "secret");
        _p.Passwords.Enqueue("secret");
        var wf = Wf(preservesEncryption: false);
        var drop = await Open(wf, path2);
        string target = P("plain.pdf");
        _p.SavePicks.Enqueue(target);
        Assert.True(await wf.SaveAsync(drop!));
        Assert.Equal(1, _p.SavePickCalls);
        Assert.Contains(Strings.Msg_EncryptionWillBeRemoved, _p.Infos);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task ConfirmClose_Clean_ReturnsTrue_Dirty_FollowsChoice()
    {
        var wf = Wf();
        var s = await wf.NewAsync(); _sessions.Add(s);
        s.MarkSaved(P("x.pdf"));
        Assert.True(await wf.ConfirmCloseAsync(s));

        s.MarkModified();
        _p.SaveChoice = UnsavedChoice.Cancel;
        Assert.False(await wf.ConfirmCloseAsync(s));
        _p.SaveChoice = UnsavedChoice.Discard;
        Assert.True(await wf.ConfirmCloseAsync(s));
    }

    [Fact]
    public async Task ConfirmClose_WhileSaveInProgress_WaitsForSave_AndDoesNotAskAgain()
    {
        var wf = Wf();
        var s = await wf.NewAsync(); _sessions.Add(s);
        s.MarkModified();
        _p.SavePicks.Enqueue(P("inflight.pdf"));
        _p.SaveChoice = UnsavedChoice.Cancel;   // 물으면 취소가 돼 false가 된다
        var saving = wf.SaveAsAsync(s);         // 저장이 아직 끝나지 않은 채로(저장 단추가 눌려 비활성인 상태)
        Assert.True(await wf.ConfirmCloseAsync(s));
        Assert.True(await saving);
        Assert.Empty(_p.OfferAllFlags);         // "저장할까요?"를 묻지 않았다
        Assert.True(File.Exists(P("inflight.pdf")));
        Assert.False(s.IsDirty);
    }

    [Fact]
    public async Task ConfirmClose_UnsavedEditAfterSave_StillAsks()
    {
        var wf = Wf();
        var s = await wf.NewAsync(); _sessions.Add(s);
        s.MarkModified();
        _p.SavePicks.Enqueue(P("then.pdf"));
        Assert.True(await wf.SaveAsAsync(s));
        s.MarkModified();                        // 저장 뒤에 또 고침
        _p.SaveChoice = UnsavedChoice.Cancel;
        Assert.False(await wf.ConfirmCloseAsync(s));
        Assert.Single(_p.OfferAllFlags);
    }

    [Fact]
    public async Task ConfirmClose_Dirty_Save_SavesThenTrue()
    {
        var wf = Wf();
        var s = await wf.NewAsync(); _sessions.Add(s);
        s.MarkModified();
        _p.SaveChoice = UnsavedChoice.Save;
        _p.SavePicks.Enqueue(P("c.pdf"));
        Assert.True(await wf.ConfirmCloseAsync(s));
        Assert.True(File.Exists(P("c.pdf")));
        // 저장 대화상자를 취소하면 닫지 않는다.
        var s2 = await wf.NewAsync(); _sessions.Add(s2);
        s2.MarkModified();
        _p.SavePicks.Enqueue(null);
        Assert.False(await wf.ConfirmCloseAsync(s2));
    }

    private async Task<List<DocumentSession>> ThreeDirty(FileWorkflow wf)
    {
        var l = new List<DocumentSession>();
        for (int i = 0; i < 3; i++)
        {
            var s = await wf.NewAsync(); _sessions.Add(s);
            s.MarkModified();
            _p.SavePicks.Enqueue(P("all" + i + ".pdf"));
            l.Add(s);
        }
        return l;
    }

    [Fact]
    public async Task SingleClose_NeverOffersAll()
    {
        var wf = Wf();
        var s = (await ThreeDirty(wf))[0];
        _p.SaveChoice = UnsavedChoice.Discard;
        Assert.True(await wf.ConfirmCloseAsync(s));
        Assert.Equal([false], _p.OfferAllFlags);   // 닫기 묶음이 아니면 문서가 여럿이어도 "모두"를 보이지 않는다
    }

    [Fact]
    public async Task Batch_OffersAllOnlyWhileMoreThanOneDirtyRemains()
    {
        var wf = Wf();
        var docs = await ThreeDirty(wf);
        using (wf.BeginCloseBatch())
        {
            foreach (var s in docs)
            {
                _p.SaveChoices.Enqueue(UnsavedChoice.Discard);
                Assert.True(await wf.ConfirmCloseAsync(s));
                wf.Release(s);
            }
        }
        Assert.Equal([true, true, false], _p.OfferAllFlags);
    }

    [Fact]
    public async Task Batch_SaveAll_SavesAllRemainingWithoutAsking()
    {
        var wf = Wf();
        var docs = await ThreeDirty(wf);
        using (wf.BeginCloseBatch())
        {
            _p.SaveChoices.Enqueue(UnsavedChoice.SaveAll);
            foreach (var s in docs)
            {
                Assert.True(await wf.ConfirmCloseAsync(s));
                wf.Release(s);
            }
        }
        Assert.Single(_p.OfferAllFlags);   // 질문은 한 번뿐
        for (int i = 0; i < 3; i++) Assert.True(File.Exists(P("all" + i + ".pdf")));
    }

    [Fact]
    public async Task Batch_DiscardAll_DiscardsRemainingWithoutSaving_AndResetsAfterBatch()
    {
        var wf = Wf();
        var docs = await ThreeDirty(wf);
        using (wf.BeginCloseBatch())
        {
            _p.SaveChoices.Enqueue(UnsavedChoice.DiscardAll);
            foreach (var s in docs)
            {
                Assert.True(await wf.ConfirmCloseAsync(s));
                wf.Release(s);
            }
        }
        Assert.Single(_p.OfferAllFlags);
        for (int i = 0; i < 3; i++) Assert.False(File.Exists(P("all" + i + ".pdf")));
        // 묶음이 끝나면 "모두" 답은 잊는다: 다음 닫기는 다시 묻는다.
        var again = await wf.NewAsync(); _sessions.Add(again);
        again.MarkModified();
        _p.SaveChoices.Enqueue(UnsavedChoice.Cancel);
        Assert.False(await wf.ConfirmCloseAsync(again));
        Assert.Equal(2, _p.OfferAllFlags.Count);
    }

    [Fact]
    public async Task Batch_Cancel_StopsAndKeepsRest()
    {
        var wf = Wf();
        var docs = await ThreeDirty(wf);
        using (wf.BeginCloseBatch())
        {
            _p.SaveChoices.Enqueue(UnsavedChoice.Discard);
            Assert.True(await wf.ConfirmCloseAsync(docs[0])); wf.Release(docs[0]);
            _p.SaveChoices.Enqueue(UnsavedChoice.Cancel);
            Assert.False(await wf.ConfirmCloseAsync(docs[1]));
        }
        Assert.Equal(2, wf.OpenSessions.Count);
    }
}
