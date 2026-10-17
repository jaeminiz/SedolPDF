using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;

namespace PdfEditor.App.Dialogs;

/// <summary>시작 시 복구 질문의 실제 구현. (화면 동작은 검증 필요: 자동 테스트 없음)</summary>
public sealed class AvaloniaRecoveryUi(Window owner) : IRecoveryUi
{
    public async Task<IReadOnlySet<Guid>?> ChooseAsync(IReadOnlyList<PdfEditor.Document.Recovery.RecoveryEntry> entries)
        => await new RecoveryDialog(entries).ShowDialog<HashSet<Guid>?>(owner);
}

/// <summary>IUserPrompts의 실제 구현. 대화상자는 소유 창 위에 모달로 띄운다. (화면 동작은 검증 필요: 자동 테스트 없음)</summary>
public sealed class AvaloniaPrompts(Window owner) : IUserPrompts
{
    private static readonly FilePickerFileType PdfType = new(Strings.Dlg_PdfFiles) { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] };

    public async Task<string?> AskPasswordAsync(string fileName, bool retry)
        => await new PasswordDialog(fileName, retry).ShowDialog<string?>(owner);

    public async Task<UnsavedChoice> AskSaveChangesAsync(string displayName)
    {
        var dlg = new MessageDialog(Strings.Dlg_UnsavedTitle, string.Format(Strings.Dlg_UnsavedMessage, displayName),
            [Strings.Dlg_Save, Strings.Dlg_Discard, Strings.Dlg_Cancel]);
        await dlg.ShowDialog(owner);
        return dlg.Result switch
        {
            0 => UnsavedChoice.Save,
            1 => UnsavedChoice.Discard,
            _ => UnsavedChoice.Cancel,
        };
    }

    public async Task<string?> PickOpenFileAsync() => (await PickOpenFilesCoreAsync(false)).FirstOrDefault();

    public async Task<IReadOnlyList<string>> PickOpenFilesAsync() => await PickOpenFilesCoreAsync(true);

    private async Task<IReadOnlyList<string>> PickOpenFilesCoreAsync(bool multiple)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Dlg_OpenTitle,
            AllowMultiple = multiple,
            FileTypeFilter = [PdfType],
        });
        return files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToList();
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.Dlg_SaveTitle,
            SuggestedFileName = suggestedName,
            DefaultExtension = "pdf",
            FileTypeChoices = [PdfType],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickSaveXfdfAsync(string suggestedName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.Dlg_ExportXfdfTitle,
            SuggestedFileName = suggestedName,
            DefaultExtension = "xfdf",
            FileTypeChoices = [new FilePickerFileType(Strings.Dlg_XfdfFiles) { Patterns = ["*.xfdf"], MimeTypes = ["application/vnd.adobe.xfdf"] }],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenXfdfAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Dlg_ImportXfdfTitle,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Strings.Dlg_XfdfFiles) { Patterns = ["*.xfdf"], MimeTypes = ["application/vnd.adobe.xfdf"] }],
        });
        return files.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null);
    }

    public async Task ShowXfdfImportResultAsync(string message, IReadOnlyList<string> details)
    {
        if (details.Count == 0)
        {
            await ShowInfoAsync(message);
            return;
        }
        var dlg = new MessageDialog(Strings.Dlg_InfoTitle, message, [Strings.Dlg_Ok, Strings.Dlg_Details]);
        await dlg.ShowDialog(owner);
        if (dlg.Result != 1) return;
        const int MaxLines = 20;   // 대화상자는 스크롤이 없어 화면 안에 들어오는 만큼만
        string text = string.Join(Environment.NewLine, details.Take(MaxLines).Select(d => d.Length > 120 ? d[..120] + "..." : d));
        if (details.Count > MaxLines) text += Environment.NewLine + "... +" + (details.Count - MaxLines);
        await new MessageDialog(Strings.Dlg_XfdfDetailsTitle, text, [Strings.Dlg_Ok]).ShowDialog(owner);
    }

    public Task ShowErrorAsync(string message)
        => new MessageDialog(Strings.Dlg_ErrorTitle, message, [Strings.Dlg_Ok]).ShowDialog(owner);

    public Task ShowInfoAsync(string message)
        => new MessageDialog(Strings.Dlg_InfoTitle, message, [Strings.Dlg_Ok]).ShowDialog(owner);

    public async Task<InsertPagesRequest?> AskInsertPagesAsync(string fileName, int sourcePageCount)
        => await new InsertPagesDialog(fileName, sourcePageCount).ShowDialog<InsertPagesRequest?>(owner);

    public async Task<MoveTarget?> AskMoveTargetAsync(int pageCount, int movingCount)
        => await new MovePagesDialog(pageCount, movingCount).ShowDialog<MoveTarget?>(owner);

    public async Task<IReadOnlyList<string>?> AskMergeFilesAsync(IReadOnlyList<string> paths)
        => await new MergeDialog(paths).ShowDialog<List<string>?>(owner);
}
