using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pdf.Native;
using PdfEditor.App.Resources;
using PdfEditor.Document;
using PdfEditor.Pages;

namespace PdfEditor.App.Documents;

/// <summary>
/// 쪽 편집 명령(삭제·회전·이동·삽입·병합·실행 취소). 대상 = 썸네일에서 고른 쪽, 없으면 현재 쪽.
/// 명령은 UI 스레드에서 실행하고 PDFium 작업은 세션 명령 스택이 전용 스레드로 보낸다.
/// CommandStack.Changed·PagesChanged는 전용 스레드에서 오므로 post로 UI 스레드에 넘겨 CanExecute를 다시 알린다.
/// </summary>
public sealed class PageCommandsViewModel : IDisposable
{
    private readonly DocumentTabViewModel _doc;
    private readonly DocumentSession _s;
    private readonly FileWorkflow _files;
    private readonly IUserPrompts _prompts;
    private readonly Action<Action> _post;
    private readonly List<IAsyncRelayCommand> _all = [];
    private bool _disposed;

    public PageCommandsViewModel(DocumentTabViewModel doc, FileWorkflow files, IUserPrompts prompts, Action<Action>? post = null)
    {
        _doc = doc;
        _s = doc.Session ?? throw new ArgumentException("Tab must have a session.", nameof(doc));
        _files = files;
        _prompts = prompts;
        _post = post ?? (a => a());

        DeleteCommand = Cmd(DeleteAsync, CanDelete);
        RotateLeftCommand = Cmd(() => RotateAsync(-1), CanEdit);
        RotateRightCommand = Cmd(() => RotateAsync(1), CanEdit);
        MoveUpCommand = Cmd(MoveUpAsync, CanMoveUp);
        MoveDownCommand = Cmd(MoveDownAsync, CanMoveDown);
        MoveToCommand = Cmd(MoveToAsync, CanEdit);
        InsertBlankCommand = Cmd(InsertBlankAsync, CanEdit);
        InsertFromFileCommand = Cmd(InsertFromFileAsync, CanEdit);
        MergeCommand = Cmd(MergeAsync, CanEdit);
        ExtractCommand = Cmd(ExtractAsync, () => !_s.Commands.IsBusy && _s.Pages.Length > 0);
        UndoCommand = Cmd(() => Run(_s.Commands.UndoAsync), () => !_s.Commands.IsBusy && _s.Commands.CanUndo);
        RedoCommand = Cmd(() => Run(_s.Commands.RedoAsync), () => !_s.Commands.IsBusy && _s.Commands.CanRedo);

        _doc.PropertyChanged += OnDocChanged;
        _s.Commands.Changed += OnStateChanged;
        _s.PagesChanged += OnPagesChanged;
    }

    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand RotateLeftCommand { get; }
    public IAsyncRelayCommand RotateRightCommand { get; }
    public IAsyncRelayCommand MoveUpCommand { get; }
    public IAsyncRelayCommand MoveDownCommand { get; }
    public IAsyncRelayCommand MoveToCommand { get; }
    public IAsyncRelayCommand InsertBlankCommand { get; }
    public IAsyncRelayCommand InsertFromFileCommand { get; }
    public IAsyncRelayCommand MergeCommand { get; }
    public IAsyncRelayCommand ExtractCommand { get; }
    public IAsyncRelayCommand UndoCommand { get; }
    public IAsyncRelayCommand RedoCommand { get; }

    /// <summary>병합 결과 문서가 만들어졌다. 화면은 이것으로 새 탭을 연다. UI 스레드에서 발생한다.</summary>
    public event Action<DocumentSession>? SessionCreated;

    /// <summary>어느 명령의 실행 가능 여부가 바뀌었을 수 있다(UI 스레드).</summary>
    public event Action? StateChanged;

    private IAsyncRelayCommand Cmd(Func<Task> run, Func<bool> can)
    {
        var c = new AsyncRelayCommand(run, can);
        _all.Add(c);
        return c;
    }

    // ---- 실행 가능 여부 ----

    private bool CanEdit() => _s.Permissions.CanEditPages && !_s.Commands.IsBusy;

    private bool CanDelete() => CanEdit() && Targets().Count < _s.Pages.Length;

    private bool CanMoveUp() => CanEdit() && TargetIndices() is { Count: > 0 } i && i[0] > 0;

    private bool CanMoveDown() => CanEdit() && TargetIndices() is { Count: > 0 } i && i[^1] < _s.Pages.Length - 1;

    private void OnDocChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentTabViewModel.SelectedPages) or nameof(DocumentTabViewModel.CurrentPageIndex)
            or nameof(DocumentTabViewModel.PageCount))
            Notify();
    }

    private void OnStateChanged() => _post(Notify);
    private void OnPagesChanged(PagesChangedEventArgs e) => _post(Notify);

    private void Notify()
    {
        if (_disposed) return;
        foreach (var c in _all) c.NotifyCanExecuteChanged();
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _doc.PropertyChanged -= OnDocChanged;
        _s.Commands.Changed -= OnStateChanged;
        _s.PagesChanged -= OnPagesChanged;
    }

    // ---- 대상 ----

    private int CurrentIndex()
    {
        var pages = _s.Pages;
        return pages.Length == 0 ? -1 : Math.Clamp(_doc.CurrentPageIndex, 0, pages.Length - 1);
    }

    /// <summary>대상 쪽(문서 순서). 고른 쪽이 없으면 현재 쪽 하나.</summary>
    private List<PageId> Targets()
    {
        var pages = _s.Pages;
        var sel = _doc.SelectedPages;
        var list = pages.Where(p => sel.Contains(p.Id)).Select(p => p.Id).ToList();
        if (list.Count == 0 && CurrentIndex() is var c and >= 0) list.Add(pages[c].Id);
        return list;
    }

    private List<int> TargetIndices()
    {
        var ids = Targets().ToHashSet();
        var res = new List<int>();
        var pages = _s.Pages;
        for (int i = 0; i < pages.Length; i++)
            if (ids.Contains(pages[i].Id)) res.Add(i);
        return res;
    }

    // ---- 실행 ----

    private async Task Run(Func<Task> body)
    {
        try { await _s.AnnotationsReady; await body(); }
        catch (Exception ex) { await _prompts.ShowErrorAsync(ErrorMessages.For(ex)); }
    }

    private void GoToPageOf(PageId id) => _post(() =>
    {
        var pages = _s.Pages;
        for (int i = 0; i < pages.Length; i++)
            if (pages[i].Id == id) { _doc.CurrentPageIndex = i; return; }
    });

    private Task DeleteAsync() => Run(async () =>
    {
        var targets = Targets();
        if (targets.Count == 0 || targets.Count >= _s.Pages.Length) return;
        await _s.Commands.ExecuteAsync(new DeletePagesCommand(targets));
    });

    private Task RotateAsync(int turns) => Run(async () =>
    {
        var targets = Targets();
        if (targets.Count == 0) return;
        await _s.Commands.ExecuteAsync(new RotatePagesCommand(targets, turns));
    });

    private Task MoveUpAsync() => Run(async () =>
    {
        var idx = TargetIndices();
        if (idx.Count == 0 || idx[0] <= 0) return;
        await MoveAsync(idx, idx[0] - 1);
    });

    private Task MoveDownAsync() => Run(async () =>
    {
        var idx = TargetIndices();
        if (idx.Count == 0 || idx[^1] >= _s.Pages.Length - 1) return;
        // 고른 쪽이 연속하지 않으면 한데 모이며, 마지막 쪽이 한 칸 내려간 자리에서 끝난다.
        await MoveAsync(idx, idx[^1] - idx.Count + 2);
    });

    private Task MoveToAsync() => Run(async () =>
    {
        var idx = TargetIndices();
        if (idx.Count == 0) return;
        int n = _s.Pages.Length;
        var target = await _prompts.AskMoveTargetAsync(n, idx.Count);
        if (target is null) return;
        int dest = target.Kind switch
        {
            MoveTargetKind.First => 0,
            MoveTargetKind.Last => n - idx.Count,
            // N쪽 앞: 옮기는 쪽을 뺀 나머지 중 N쪽보다 앞에 있는 쪽 수가 결과 위치다.
            _ => Enumerable.Range(0, Math.Clamp(target.PageNumber, 1, n) - 1).Count(i => !idx.Contains(i)),
        };
        await MoveAsync(idx, dest);
    });

    private async Task MoveAsync(List<int> indices, int dest)
    {
        var pages = _s.Pages;
        var ids = indices.Select(i => pages[i].Id).ToList();
        await _s.Commands.ExecuteAsync(new MovePagesCommand(ids, dest));
        GoToPageOf(ids[0]);
    }

    private Task InsertBlankAsync() => Run(async () =>
    {
        int cur = CurrentIndex();
        if (cur < 0) return;
        var p = _s.Pages[cur];
        await _s.Commands.ExecuteAsync(new InsertBlankPageCommand(cur + 1, p.WidthPt, p.HeightPt));
        _post(() => _doc.CurrentPageIndex = cur + 1);
    });

    // ---- 파일에서 삽입 ----

    private Task InsertFromFileAsync() => Run(async () =>
    {
        string? path = await _prompts.PickOpenFileAsync();
        if (path is null) return;
        var opened = await OpenSourceAsync(path);
        if (opened is null) return;
        var (src, srcBytes, srcPassword) = opened.Value;
        bool handedOver = false;
        try
        {
            int srcCount = await _s.Thread.InvokeAsync(() => src.PageCount);
            var req = await _prompts.AskInsertPagesAsync(Path.GetFileName(path), srcCount);
            if (req is null) return;
            if (!PageRangeParser.TryParse(req.RangeText, srcCount, out var indices) || indices.Length == 0)
            {
                await _prompts.ShowErrorAsync(Strings.Err_PageRangeInvalid);
                return;
            }
            int n = _s.Pages.Length;
            int at = req.Position switch
            {
                InsertPosition.First => 0,
                InsertPosition.Last => n,
                _ => Math.Min(n, CurrentIndex() + 1),
            };
            // 가져온 쪽의 원본 주석: 읽지 못하면 null (표시만 됨).
            var originals = await OriginalAnnotationLoader.ReadAsync(_s.Thread, src, srcBytes, srcPassword, indices);
            if (originals is null) _s.MarkOriginalAnnotationsReadOnly();
            await _s.Commands.ExecuteAsync(new ImportPagesCommand(src, indices, at, originals));
            handedOver = true;   // 이후 원본 문서는 명령이 소유한다
            _post(() => _doc.CurrentPageIndex = at);
        }
        finally
        {
            // PdfDocument.Dispose는 PDFium 스레드 작업을 동기로 기다리므로 UI 스레드에서 부르지 않고 전용 스레드에서 닫는다.
            if (!handedOver) await _s.Thread.InvokeAsync(src.Dispose);
        }
    });


    // ---- 페이지 추출 (2026-10-20 사용자 요청) ----

    private Task ExtractAsync() => Run(async () =>
    {
        var idx = TargetIndices();
        if (idx.Count == 0) return;
        string baseName = Path.GetFileNameWithoutExtension(_s.DisplayName);
        string range = idx.Count == 1 ? $"{idx[0] + 1}" : $"{idx[0] + 1}-{idx[^1] + 1}";
        string? target = await _prompts.PickSaveFileAsync(string.Format(Strings.Doc_ExtractSuggestedName, baseName, range));
        if (target is null) return;
        var svc = DocumentTabViewModel.Appearance;
        var output = new PdfEditor.Document.Output.OutputCopyBuilder(svc?.Appearance, svc?.Patcher, svc?.Assets);
        await PdfEditor.Document.Output.PageExtractor.ExtractAsync(_s, output, idx, Path.GetFullPath(target));
        await _prompts.ShowInfoAsync(string.Format(Strings.Msg_ExtractDone, idx.Count, target));
    });

    // ---- 끌어다 놓기로 쪽 추가 (2026-10-20 사용자 요청) ----

    /// <summary>PDF 파일들의 모든 쪽을 at(쪽 번호 0부터) 자리에 차례로 넣는다. 암호가 있으면 묻는다. 실행 취소는 파일마다 한 번.</summary>
    public Task InsertFilesAtAsync(IReadOnlyList<string> paths, int at) => Run(async () =>
    {
        if (!CanEdit() || paths.Count == 0) return;
        int pos = Math.Clamp(at, 0, _s.Pages.Length);
        int first = pos;
        foreach (var path in paths)
        {
            var opened = await OpenSourceAsync(path);
            if (opened is null) continue;
            var (src, srcBytes, srcPassword) = opened.Value;
            bool handedOver = false;
            try
            {
                int n = await _s.Thread.InvokeAsync(() => src.PageCount);
                if (n == 0) continue;
                int[] indices = [.. Enumerable.Range(0, n)];
                var originals = await OriginalAnnotationLoader.ReadAsync(_s.Thread, src, srcBytes, srcPassword, indices);
                if (originals is null) _s.MarkOriginalAnnotationsReadOnly();
                pos = Math.Clamp(pos, 0, _s.Pages.Length);
                await _s.Commands.ExecuteAsync(new ImportPagesCommand(src, indices, pos, originals));
                handedOver = true;
                pos += n;
            }
            finally
            {
                if (!handedOver) await _s.Thread.InvokeAsync(src.Dispose);
            }
        }
        if (pos > first) _post(() => _doc.CurrentPageIndex = first);
    });
    /// <summary>파일을 읽어 열고, 암호가 필요하면 묻는다. 취소하거나 열지 못하면 null (오류는 이미 보여 줌).</summary>
    private async Task<(PdfDocument Doc, byte[] Bytes, string? Password)?> OpenSourceAsync(string path)
    {
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(path); }
        catch (Exception ex) { await _prompts.ShowErrorAsync(ErrorMessages.For(ex)); return null; }

        string name = Path.GetFileName(path);
        string? password = null;
        bool retry = false;
        while (true)
        {
            try
            {
                var t = _s.Thread;
                return (await t.InvokeAsync(() => PdfDocument.OpenBytes(t, bytes, password)), bytes, password);
            }
            catch (PdfPasswordRequiredException)
            {
                password = await _prompts.AskPasswordAsync(name, retry);
                if (password is null) return null;
                retry = true;
            }
            catch (Exception ex)
            {
                await _prompts.ShowErrorAsync(ErrorMessages.For(ex));
                return null;
            }
        }
    }

    // ---- 병합 ----

    private Task MergeAsync() => Run(async () =>
    {
        var picked = await _prompts.PickOpenFilesAsync();
        if (picked.Count == 0) return;
        var order = await _prompts.AskMergeFilesAsync(picked);
        if (order is null || order.Count == 0) return;

        var sources = new List<MergeSource>();
        foreach (var path in order)
        {
            var pw = await ResolvePasswordAsync(path);
            if (!pw.Ok) return;
            sources.Add(new MergeSource(path, pw.Password));
        }

        var session = await MergeService.CreateMergedAsync(_s.Thread, sources, _files.NextUntitledName(), CancellationToken.None);
        _files.Adopt(session);
        SessionCreated?.Invoke(session);
    });

    /// <summary>열어 보고 암호가 필요하면 묻는다. 사용자가 취소하면 Ok=false. 열 수 없는 파일은 병합 단계에서 오류로 드러난다.</summary>
    private async Task<(bool Ok, string? Password)> ResolvePasswordAsync(string path)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path);
        string name = Path.GetFileName(path);
        string? password = null;
        bool retry = false;
        var t = _s.Thread;
        while (true)
        {
            try
            {
                await t.InvokeAsync(() => { using var d = PdfDocument.OpenBytes(t, bytes, password); });
                return (true, password);
            }
            catch (PdfPasswordRequiredException)
            {
                password = await _prompts.AskPasswordAsync(name, retry);
                if (password is null) return (false, null);
                retry = true;
            }
        }
    }
}
