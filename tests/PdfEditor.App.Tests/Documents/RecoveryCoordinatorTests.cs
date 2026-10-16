using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Settings;
using PdfEditor.Document;
using PdfEditor.Document.Recovery;
using PdfEditor.Document.Saving;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Documents;

public sealed class FakeRecoveryUi : IRecoveryUi
{
    /// <summary>null이면 전부 복구. 비어 있으면 전부 버림.</summary>
    public HashSet<Guid>? Restore { get; set; }
    /// <summary>true면 "나중에"(null)로 답한다.</summary>
    public bool AnswerLater { get; set; }
    public int Calls { get; private set; }
    public Task<IReadOnlySet<Guid>?> ChooseAsync(IReadOnlyList<RecoveryEntry> entries)
    {
        Calls++;
        if (AnswerLater) return Task.FromResult<IReadOnlySet<Guid>?>(null);
        return Task.FromResult<IReadOnlySet<Guid>?>(Restore ?? entries.Select(e => e.SessionId).ToHashSet());
    }
}

public class RecoveryCoordinatorTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-rc-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly FakePrompts _p = new();
    private readonly FakeRecoveryUi _ui = new();
    private readonly List<DocumentSession> _sessions = [];
    private SettingsStore _settings = null!;
    private RecoveryStore _store = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
        _store = new RecoveryStore(Path.Combine(_dir, "recovery"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions) await s.DisposeAsync();
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);
    private FileWorkflow Wf() => new(_t, new SaveService([]), _settings, _p);

    private RecoveryCoordinator Coord(FileWorkflow wf, Func<DocumentSession, bool>? canWrite = null)
    {
        var c = new RecoveryCoordinator(_t, _store, _settings, _ui, _p, canWrite);
        c.Attach(wf);
        return c;
    }

    private async Task<DocumentSession> OpenDirty(FileWorkflow wf, string path, int pages = 3)
    {
        TestPdfFactory.CreateBlank(path, pages, 300, 400);
        var s = (await wf.OpenAsync(path))!;
        _sessions.Add(s);
        await s.Commands.ExecuteAsync(new DeletePagesCommand([s.Pages[0].Id]));
        return s;
    }

    [Fact]
    public async Task Coordinator_SkipsCleanSessions()
    {
        var wf = Wf();
        string path = P("a.pdf");
        TestPdfFactory.CreateBlank(path, 2, 300, 400);
        _sessions.Add((await wf.OpenAsync(path))!);
        var c = Coord(wf);
        Assert.Equal(0, await c.WriteDirtyAsync(wf.OpenSessions));
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task Coordinator_WritesDirtySessions()
    {
        var wf = Wf();
        var s = await OpenDirty(wf, P("a.pdf"));
        Assert.Equal(1, await Coord(wf).WriteDirtyAsync(wf.OpenSessions));
        Assert.Equal(s.Id, Assert.Single(_store.List()).SessionId);
    }

    [Fact]
    public async Task Coordinator_SkipsEncrypted_WhenNotPreserved()
    {
        var wf = Wf();
        string path = P("enc.pdf");
        TestPdfFactory.CreateEncrypted(path, 3, 300, 400, "secret");
        _p.Passwords.Enqueue("secret");
        var s = (await wf.OpenAsync(path))!;
        _sessions.Add(s);
        await s.Commands.ExecuteAsync(new DeletePagesCommand([s.Pages[0].Id]));

        // 암호화가 유지되지 않는 정책이면 암호 없는 사본을 남기지 않는다
        Assert.Equal(0, await Coord(wf, _ => false).WriteDirtyAsync(wf.OpenSessions));
        Assert.Empty(_store.List());

        // 기본 정책(SavePolicy.CanWriteRecovery == true): 기록되고, 복구본도 암호가 필요하다
        Assert.True(SavePolicy.CanWriteRecovery(s));
        Assert.Equal(1, await Coord(wf).WriteDirtyAsync(wf.OpenSessions));
        var e = Assert.Single(_store.List());
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => DocumentSession.OpenAsync(_t, e.RecoveryPdfPath, null));
    }

    [Fact]
    public async Task Restore_SameHash_BindsToOriginalAndDirty()
    {
        string path = P("a.pdf");
        var wf0 = Wf();
        var s0 = await OpenDirty(wf0, path);
        await _store.WriteAsync(s0);
        await s0.DisposeAsync();
        _sessions.Remove(s0);

        var wf = Wf();
        var restored = await Coord(wf).RecoverAtStartupAsync();
        var s = Assert.Single(restored);
        _sessions.Add(s);
        Assert.Equal(Path.GetFullPath(path), s.FilePath);
        Assert.True(s.IsDirty);
        Assert.Equal(2, s.Pages.Length);
        Assert.Empty(_p.Infos);
        // 복구한 세션이 새 복구 항목으로 이어지고 옛 항목은 사라진다
        var e = Assert.Single(_store.List());
        Assert.Equal(s.Id, e.SessionId);
        Assert.Contains(s, wf.OpenSessions);
    }

    [Fact]
    public async Task Restore_ChangedHash_OpensUntitled()
    {
        string path = P("a.pdf");
        var wf0 = Wf();
        var s0 = await OpenDirty(wf0, path);
        await _store.WriteAsync(s0);
        await s0.DisposeAsync();
        _sessions.Remove(s0);
        TestPdfFactory.CreateBlank(path, 7, 200, 200);   // 원본이 바뀜
        byte[] changed = File.ReadAllBytes(path);

        var s = Assert.Single(await Coord(Wf()).RecoverAtStartupAsync());
        _sessions.Add(s);
        Assert.Null(s.FilePath);
        Assert.Equal(string.Format(Strings.Doc_Recovered, "a.pdf"), s.DisplayName);
        Assert.True(s.IsDirty);
        Assert.Equal(2, s.Pages.Length);
        Assert.Contains(Strings.Msg_RecoveryOriginalChanged, _p.Infos);
        Assert.Equal(changed, File.ReadAllBytes(path));   // 바뀐 원본은 건드리지 않는다
    }

    [Fact]
    public async Task Restore_Untitled_Twice_NoDoubleSuffix()
    {
        string path = P("a.pdf");
        var s0 = await OpenDirty(Wf(), path);
        await _store.WriteAsync(s0);
        await s0.DisposeAsync();
        _sessions.Remove(s0);
        File.Delete(path);

        var s1 = Assert.Single(await Coord(Wf()).RecoverAtStartupAsync());
        await s1.DisposeAsync();   // 다시 비정상 종료한 것처럼: 복구 항목은 남아 있다
        var s2 = Assert.Single(await Coord(Wf()).RecoverAtStartupAsync());
        _sessions.Add(s2);
        Assert.Equal(string.Format(Strings.Doc_Recovered, "a.pdf"), s2.DisplayName);
    }

    [Fact]
    public async Task Restore_OriginalMissing_OpensUntitled()
    {
        string path = P("a.pdf");
        var wf0 = Wf();
        var s0 = await OpenDirty(wf0, path);
        await _store.WriteAsync(s0);
        await s0.DisposeAsync();
        _sessions.Remove(s0);
        File.Delete(path);

        var s = Assert.Single(await Coord(Wf()).RecoverAtStartupAsync());
        _sessions.Add(s);
        Assert.Null(s.FilePath);
        Assert.Contains(Strings.Msg_RecoveryOriginalChanged, _p.Infos);
    }

    [Fact]
    public async Task Recover_DiscardAndLater()
    {
        var wf0 = Wf();
        var s0 = await OpenDirty(wf0, P("a.pdf"));
        await _store.WriteAsync(s0);

        _ui.AnswerLater = true;
        Assert.Empty(await Coord(Wf()).RecoverAtStartupAsync());
        Assert.Single(_store.List());     // 나중에: 항목 유지

        _ui.AnswerLater = false;
        _ui.Restore = [];                 // 전부 버리기
        Assert.Empty(await Coord(Wf()).RecoverAtStartupAsync());
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task Recover_NoEntries_DoesNotAsk()
    {
        Assert.Empty(await Coord(Wf()).RecoverAtStartupAsync());
        Assert.Equal(0, _ui.Calls);
    }

    [Fact]
    public async Task SaveSuccess_DeletesRecoveryFile()
    {
        var wf = Wf();
        var s = await OpenDirty(wf, P("a.pdf"));
        var c = Coord(wf);
        await c.WriteDirtyAsync(wf.OpenSessions);
        Assert.Single(_store.List());
        Assert.True(await wf.SaveAsync(s));
        Assert.Empty(_store.List());
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "recovery")));
    }

    [Fact]
    public async Task NormalClose_DeletesRecoveryFile()
    {
        var wf = Wf();
        var s = await OpenDirty(wf, P("a.pdf"));
        var c = Coord(wf);
        await c.WriteDirtyAsync(wf.OpenSessions);
        wf.Release(s);
        Assert.Empty(_store.List());
    }

    [Theory]
    [InlineData(0, 5)]      // 0 이하는 기본값 5
    [InlineData(-3, 5)]
    [InlineData(1, 1)]
    [InlineData(60, 60)]
    [InlineData(99, 60)]
    public void Interval_IsClampedToOneToSixty(int minutes, int expected)
    {
        _settings.Save(new AppSettings([], minutes));
        Assert.Equal(TimeSpan.FromMinutes(expected), RecoveryCoordinator.IntervalOf(_settings));
    }
}
