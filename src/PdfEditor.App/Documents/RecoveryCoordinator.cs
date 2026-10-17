using System.Security.Cryptography;
using Pdf.Native;
using PdfEditor.App.Resources;
using PdfEditor.App.Settings;
using PdfEditor.Document;
using PdfEditor.Document.Recovery;
using PdfEditor.Document.Saving;

namespace PdfEditor.App.Documents;

/// <summary>시작 시 복구 질문. 항목마다 복구/버리기를 고른다.</summary>
public interface IRecoveryUi
{
    /// <summary>복구할 항목의 SessionId 집합을 돌려준다(나머지는 버림). null이면 "나중에": 아무것도 지우지 않는다.</summary>
    Task<IReadOnlySet<Guid>?> ChooseAsync(IReadOnlyList<RecoveryEntry> entries);
}

/// <summary>
/// 자동 복구: 설정 간격마다 변경된 세션을 RecoveryStore에 기록하고, 저장·정상 닫기 때 지우고,
/// 시작할 때 남은 항목을 복구한다. 기록은 최선 노력이라 실패해도 작업을 막지 않는다.
/// </summary>
public sealed class RecoveryCoordinator(PdfiumThread t, RecoveryStore store, SettingsStore settings, IRecoveryUi ui,
    IUserPrompts prompts, Func<DocumentSession, bool>? canWrite = null) : IDisposable
{
    public const int MinMinutes = 1, MaxMinutes = 60;

    private readonly Func<DocumentSession, bool> _canWrite = canWrite ?? SavePolicy.CanWriteRecovery;
    private readonly object _gate = new();
    private readonly HashSet<Guid> _released = [];
    private FileWorkflow? _workflow;
    private Func<Task<IReadOnlyList<DocumentSession>>>? _snapshot;
    private Timer? _timer;
    private bool _disposed;

    public static TimeSpan IntervalOf(SettingsStore s)
        => TimeSpan.FromMinutes(Math.Clamp(s.Load().RecoveryIntervalMinutes, MinMinutes, MaxMinutes));

    /// <summary>저장 성공·정상 닫기에 복구 파일을 지우도록 연결하고, 복구한 세션을 넣을 곳을 정한다.</summary>
    public void Attach(FileWorkflow wf)
    {
        _workflow = wf;
        wf.Saved += s => store.Delete(s.Id);
        wf.Released += s =>
        {
            lock (_gate) _released.Add(s.Id);
            store.Delete(s.Id);
        };
    }

    /// <summary>변경된 세션만 기록한다. 기록한 세션 수를 돌려준다.</summary>
    public async Task<int> WriteDirtyAsync(IReadOnlyList<DocumentSession> sessions)
    {
        int written = 0;
        foreach (var s in sessions)
        {
            if (!s.IsDirty || !_canWrite(s)) continue;
            bool released;
            lock (_gate) released = _released.Contains(s.Id);
            if (released) continue;
        try
            {
                await store.WriteAsync(s).ConfigureAwait(false);
                written++;
                // 기록하는 동안 저장되었거나 닫혔다면 방금 쓴 파일은 낡은 것이다
                lock (_gate) released = _released.Contains(s.Id);
                if (released || !s.IsDirty) store.Delete(s.Id);
            }
            catch (Exception) { /* 복구 기록 실패는 작업을 막지 않는다 (다음 주기에 다시 시도) */ }
        }
        return written;
    }

    /// <summary>주기 기록을 시작한다. snapshot은 열린 세션 목록을 UI 스레드에서 가져온다. 간격은 매번 설정에서 다시 읽는다.</summary>
    public void Start(Func<Task<IReadOnlyList<DocumentSession>>> snapshot)
    {
        _snapshot = snapshot;
        lock (_gate)
        {
            if (_disposed) return;
            _timer ??= new Timer(_ => _ = TickAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(IntervalOf(settings), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>환경 설정에서 간격을 바꾼 뒤 부른다.</summary>
    public void Reschedule()
    {
        lock (_gate)
        {
            if (!_disposed) _timer?.Change(IntervalOf(settings), Timeout.InfiniteTimeSpan);
        }
    }

    private async Task TickAsync()
    {
        try
        {
            if (_snapshot is not null) await WriteDirtyAsync(await _snapshot().ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Exception) { /* 종료 중 등 */ }
        finally { Reschedule(); }
    }

    /// <summary>
    /// 시작할 때 남은 항목이 있으면 묻고, 고른 것을 연다. 열린 세션을 돌려주므로 호출자가 탭으로 보여 준다.
    /// 원본 해시가 같으면 원본에 연결(더티), 아니면 "{이름} (복구됨)" 제목 없는 문서로 연다.
    /// </summary>
    public async Task<IReadOnlyList<DocumentSession>> RecoverAtStartupAsync()
    {
        store.SweepStrayTemps();   // 강제 종료가 남긴 오래된 임시 파일 정리 (예외 없음)
        var entries = store.List();
        if (entries.Count == 0) return [];
        var chosen = await ui.ChooseAsync(entries).ConfigureAwait(true);
        if (chosen is null) return [];

        var opened = new List<DocumentSession>();
        foreach (var e in entries)
        {
            if (!chosen.Contains(e.SessionId)) { store.Delete(e.SessionId); continue; }
            var s = await RestoreAsync(e).ConfigureAwait(true);
            if (s is null) continue;   // 열지 못한 항목은 지우지 않는다
            opened.Add(s);
        }
        return opened;
    }

    private async Task<DocumentSession?> RestoreAsync(RecoveryEntry e)
    {
        bool bind = await OriginalUnchangedAsync(e).ConfigureAwait(true);
        DocumentSession? s = null;
        string? password = null;
        bool retry = false;
        while (s is null)
        {
        try
            {
                s = await DocumentSession.OpenRecoveredAsync(t, e, bind, password, deferOriginals: true).ConfigureAwait(true);
            }
            catch (PdfPasswordRequiredException)
            {
                password = await prompts.AskPasswordAsync(e.DisplayName, retry).ConfigureAwait(true);
                if (password is null) return null;
                retry = true;
            }
            catch (Exception ex)
            {
                await prompts.ShowErrorAsync(ErrorMessages.For(ex)).ConfigureAwait(true);
                return null;
            }
        }

        if (!bind)
        {
            // 복구본을 또 복구할 때 "(복구됨) (복구됨)"처럼 겹치지 않게 한다
            string suffix = string.Format(Strings.Doc_Recovered, "");
            s.DisplayName = e.DisplayName.EndsWith(suffix, StringComparison.Ordinal) ? e.DisplayName : string.Format(Strings.Doc_Recovered, e.DisplayName);
            if (e.OriginalPath is not null)
                await prompts.ShowInfoAsync(Strings.Msg_RecoveryOriginalChanged).ConfigureAwait(true);
        }
        _workflow?.Adopt(s);
        // 복구한 세션이 새 항목으로 이어진 뒤에 옛 항목을 지운다 (그 사이 또 종료돼도 잃지 않도록)
        try { await store.WriteAsync(s).ConfigureAwait(true); } catch (Exception) { }
        store.Delete(e.SessionId);
        return s;
    }

    private static async Task<bool> OriginalUnchangedAsync(RecoveryEntry e)
    {
        if (e.OriginalPath is null || e.OriginalSha256Hex is null || !File.Exists(e.OriginalPath)) return false;
        try
        {
            var hash = SHA256.HashData(await File.ReadAllBytesAsync(e.OriginalPath).ConfigureAwait(false));
            return string.Equals(Convert.ToHexString(hash), e.OriginalSha256Hex, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
