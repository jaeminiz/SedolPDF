using Pdf.Native;
using PdfEditor.App.Resources;
using PdfEditor.App.Settings;
using PdfEditor.Document;
using PdfEditor.Document.Saving;

namespace PdfEditor.App.Documents;

public enum UnsavedChoice { Save, Discard, Cancel }

public enum InsertPosition { AfterCurrent, First, Last }

/// <summary>파일에서 삽입 대화상자의 결과. RangeText는 사용자가 쓴 쪽 범위(비어 있으면 전체).</summary>
public sealed record InsertPagesRequest(InsertPosition Position, string RangeText);

public enum MoveTargetKind { BeforePage, First, Last }

/// <summary>위치 지정 이동 대화상자의 결과. BeforePage이면 PageNumber(1부터)쪽 앞으로.</summary>
public sealed record MoveTarget(MoveTargetKind Kind, int PageNumber);

/// <summary>대화상자를 추상화해서 테스트에서 가짜로 바꿀 수 있게 한다. 실제 구현은 Dialogs/AvaloniaPrompts.</summary>
public interface IUserPrompts
{
    /// <summary>암호를 묻는다. 취소하면 null. retry가 true면 앞서 입력한 암호가 틀렸다는 뜻.</summary>
    Task<string?> AskPasswordAsync(string fileName, bool retry);
    Task<UnsavedChoice> AskSaveChangesAsync(string displayName);
    Task<string?> PickOpenFileAsync();
    Task<IReadOnlyList<string>> PickOpenFilesAsync();
    Task<string?> PickSaveFileAsync(string suggestedName);
    Task ShowErrorAsync(string message);
    Task ShowInfoAsync(string message);
    /// <summary>파일에서 삽입 대화상자. 취소하면 null. 쪽 범위가 틀리면 대화상자 안에서 알리고 닫지 않는다.</summary>
    Task<InsertPagesRequest?> AskInsertPagesAsync(string fileName, int sourcePageCount);
    /// <summary>위치 지정 이동 대화상자. 취소하면 null.</summary>
    Task<MoveTarget?> AskMoveTargetAsync(int pageCount, int movingCount);
    /// <summary>병합 대화상자(순서 바꾸기·빼기). 취소하면 null, 확인하면 병합할 순서의 경로 목록.</summary>
    Task<IReadOnlyList<string>?> AskMergeFilesAsync(IReadOnlyList<string> paths);
}

/// <summary>열기·저장·닫기 흐름. UI 스레드에서 부르고, 사용자에게 묻는 일은 IUserPrompts로 한다.</summary>
public sealed class FileWorkflow(PdfiumThread t, SaveService save, SettingsStore settings, IUserPrompts prompts,
    bool preservesEncryption = SavePolicy.PreservesEncryption)
{
    private readonly List<DocumentSession> _open = [];
    private int _untitledCounter;

    // SavePolicy.CanOverwrite와 같은 규칙. 암호화가 유지되지 않는 경우를 테스트하려고 값을 주입할 수 있게 했다.
    private bool CanOverwrite(DocumentSession s) => !s.IsEncrypted || preservesEncryption;

    public IReadOnlyList<string> RecentFiles => settings.Load().RecentFiles;

    /// <summary>다른 곳에서 만든 세션(병합 결과)을 열린 세션 목록에 넣는다.</summary>
    public void Adopt(DocumentSession s) => _open.Add(s);

    public string NextUntitledName() => string.Format(Strings.Doc_Untitled, ++_untitledCounter);

    /// <summary>쪽 하나짜리 새 문서를 만든다. 열린 세션 목록에 추가된다.</summary>
    public async Task<DocumentSession> NewAsync()
    {
        var s = await DocumentSession.CreateNewAsync(t).ConfigureAwait(true);
        s.DisplayName = NextUntitledName();
        _open.Add(s);
        return s;
    }

    /// <summary>
    /// 파일을 연다. 이미 열린 경로면 그 세션을 돌려준다(호출자가 탭을 전환한다).
    /// 실패하면 오류를 보여 주고 null, 암호 입력을 취소해도 null.
    /// </summary>
    public async Task<DocumentSession?> OpenAsync(string path)
    {
        string full = Path.GetFullPath(path);
        var existing = _open.FirstOrDefault(s => PathEquals(s.FilePath, full));
        if (existing is not null) return existing;

        string name = Path.GetFileName(full);
        string? password = null;
        bool retry = false;
        DocumentSession? session = null;
        while (session is null)
        {
            try
            {
                session = await DocumentSession.OpenAsync(t, full, password).ConfigureAwait(true);
            }
            catch (PdfPasswordRequiredException)
            {
                password = await prompts.AskPasswordAsync(name, retry).ConfigureAwait(true);
                if (password is null) return null;
                retry = true;
            }
            catch (Exception ex)
            {
                await prompts.ShowErrorAsync(ErrorMessages.For(ex)).ConfigureAwait(true);
                return null;
            }
        }

        _open.Add(session);
        try { settings.AddRecent(full); } catch (Exception) { /* 최근 문서 기록 실패는 열기를 막지 않는다 */ }
        if (session.WasRepaired) await prompts.ShowInfoAsync(Strings.Msg_OpenedRepaired).ConfigureAwait(true);
        return session;
    }

    /// <summary>탭을 닫을 때 호출해 세션을 목록에서 뺀다. 세션 해제는 호출자가 한다.</summary>
    public void Release(DocumentSession s) => _open.Remove(s);

    public async Task<bool> SaveAsync(DocumentSession s)
    {
        if (s.FilePath is null || !CanOverwrite(s)) return await SaveAsAsync(s).ConfigureAwait(true);
        return await TrySaveAsync(s, s.FilePath).ConfigureAwait(true);
    }

    public async Task<bool> SaveAsAsync(DocumentSession s)
    {
        // 암호화가 유지되지 않으면 사용자에게 먼저 알린다 (V6, 설계서 8.2).
        if (s.IsEncrypted && !preservesEncryption)
            await prompts.ShowInfoAsync(Strings.Msg_EncryptionWillBeRemoved).ConfigureAwait(true);
        string? target = await prompts.PickSaveFileAsync(SuggestedName(s)).ConfigureAwait(true);
        if (target is null) return false;
        return await TrySaveAsync(s, Path.GetFullPath(target)).ConfigureAwait(true);
    }

    private async Task<bool> TrySaveAsync(DocumentSession s, string path)
    {
        try
        {
            await save.SaveAsync(s, path).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await prompts.ShowErrorAsync(ErrorMessages.For(ex)).ConfigureAwait(true);
            return false;
        }
        try { settings.AddRecent(path); } catch (Exception) { }
        return true;
    }

    /// <summary>닫아도 되면 true. 변경이 있으면 저장/저장 안 함/취소를 묻는다.</summary>
    public async Task<bool> ConfirmCloseAsync(DocumentSession s)
    {
        if (!s.IsDirty) return true;
        switch (await prompts.AskSaveChangesAsync(s.DisplayName).ConfigureAwait(true))
        {
            case UnsavedChoice.Save: return await SaveAsync(s).ConfigureAwait(true);
            case UnsavedChoice.Discard: return true;
            default: return false;
        }
    }

    private static string SuggestedName(DocumentSession s)
    {
        string n = s.DisplayName;
        return n.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? n : n + ".pdf";
    }

    private static bool PathEquals(string? a, string b) => a is not null && string.Equals(Path.GetFullPath(a), b, StringComparison.OrdinalIgnoreCase);
}
