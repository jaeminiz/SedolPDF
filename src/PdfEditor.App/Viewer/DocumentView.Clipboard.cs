using Avalonia.Controls;
using Avalonia.Input;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.App.Viewer.Objects;
using PdfEditor.Document;
using PdfEditor.Document.Content;

namespace PdfEditor.App.Viewer;

// 개체·주석 복사/잘라내기/붙여넣기/복제(0.9.0): 단축키 Ctrl+C/X/V/D와 오른쪽 클릭 메뉴. 계산·명령은 ClipboardController(Avalonia 없음).
public sealed partial class DocumentView
{
    private static bool UsesClipboardTools(ToolKind t) => t is ToolKind.EditObject or ToolKind.AnnotSelect;

    private ClipboardController ObjClipboard => ClipboardController.Shared;

    /// <summary>
    /// Ctrl+C/X/V/D. 객체 편집·주석 선택 도구에서만, 글 입력 중이 아닐 때만. 고른 개체·주석이 없으면 Ctrl+C는 지금처럼 글자 복사로 넘어간다(false).
    /// </summary>
    private bool HandleClipboardKey(KeyEventArgs e)
    {
        if (_state is not { } st || e.KeyModifiers != KeyModifiers.Control) return false;
        if (e.Key is not (Key.C or Key.X or Key.V or Key.D)) return false;
        if (!UsesClipboardTools(st.Tab.Tool) || IsLineEditorOpen || IsFreeTextEditorOpen || IsParagraphEditorOpen) return false;
        var tab = st.Tab;
        switch (e.Key)
        {
            case Key.C or Key.X:
                if (!ClipboardController.HasSelection(tab)) return false;
                bool cut = e.Key == Key.X;
                _ = SafeAsync(() => ObjClipboard.CopyAsync(tab, cut));
                break;
            case Key.V:
                if (ObjClipboard.Current is null) return false;
                _ = SafeAsync(() => PasteToCurrentPageAsync(st));
                break;
            default:
                if (!ClipboardController.HasSelection(tab)) return false;
                _ = SafeAsync(() => DuplicateSelectionAsync(st));
                break;
        }
        e.Handled = true;
        return true;
    }

    private static async Task SafeAsync(Func<Task> body)
    {
        try { await body(); }
        catch (Exception) { }   // 쪽이 그사이 지워짐·PDFium 스레드가 닫힘: 아무것도 바뀌지 않는다
    }

    private async Task PasteToCurrentPageAsync(ViewState st) => await PasteToPageAsync(st, CurrentPageInfo(st));

    private PageInfo? CurrentPageInfo(ViewState st)
    {
        var pages = st.Session.Pages;
        if (pages.Length == 0) return null;
        return pages[Math.Clamp(st.Tab.CurrentPageIndex, 0, pages.Length - 1)];
    }

    private async Task PasteToPageAsync(ViewState st, PageInfo? info)
    {
        if (info is null) return;
        int rot = await PageRotationAsync(st, info.Id);
        await ObjClipboard.PasteAsync(st.Tab, info.Id, rot);
        if (!st.Disposed) InvalidateVisual();
    }

    private async Task DuplicateSelectionAsync(ViewState st)
    {
        PageId? page = st.Tab.Objects.Selected?.Page ?? st.Tab.Selection.Page;
        if (page is not { } p) return;
        int rot = await PageRotationAsync(st, p);
        await ObjClipboard.DuplicateAsync(st.Tab, rot);
        if (!st.Disposed) InvalidateVisual();
    }

    private static Task<int> PageRotationAsync(ViewState st, PageId id) =>
        st.Session.Thread.InvokeAsync(() => st.Session.IndexOf(id) < 0 ? 0 : st.Session.GetLoadedPage(id).Rotation);

    /// <summary>객체 편집·주석 선택 도구의 오른쪽 클릭 메뉴: 잘라내기 / 복사 / 붙여넣기 / 복제 / 삭제. 쓸 수 없는 항목은 흐리게.</summary>
    private void ShowObjectMenu(PointerPressedEventArgs e)
    {
        if (_state is not { } st) return;
        var tab = st.Tab;
        int pageIdx = _layout?.HitTest(e.GetPosition(this))?.Page ?? -1;
        PageInfo? info = pageIdx >= 0 && pageIdx < _layoutPages.Length ? _layoutPages[pageIdx] : null;

        // 아무것도 안 골랐으면 누른 자리의 개체를 먼저 고른다(객체 편집 도구)
        if (tab.Tool == ToolKind.EditObject && !ClipboardController.HasSelection(tab) && info is not null && _layout is { } lay
            && TryGetGeom(st, info, out var g) && TryGetObjectIndex(st, info, out var idx))
        {
            var pt = ToPdf(g, pageIdx, e.GetPosition(this), lay.Scale);
            var pick = tab.Objects.Pick(idx, pt, 4 * (float)(1.0 / lay.Scale), false, null);
            if (pick is not null && !pick.IsFrame)
            {
                tab.Objects.Select(new SelectedObject(info.Id, pick.Ref, pick.Type, pick.Bounds));
                InvalidateVisual();
            }
        }

        bool has = ClipboardController.HasSelection(tab);
        bool canPaste = ObjClipboard.CanPaste(tab) && info is not null;

        MenuItem Item(string header, Action act, bool enabled, Key key)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled, InputGesture = new KeyGesture(key, KeyModifiers.Control) };
            mi.Click += (_, _) => act();
            return mi;
        }
        var del = new MenuItem { Header = Strings.Menu_Delete, IsEnabled = has, InputGesture = new KeyGesture(Key.Delete) };
        del.Click += (_, _) => DeleteSelectionFromMenu(st);
        var items = new List<Control>
        {
            Item(Strings.Menu_Cut, () => _ = SafeAsync(() => ObjClipboard.CopyAsync(tab, cut: true)), has, Key.X),
            Item(Strings.Menu_Copy, () => _ = SafeAsync(() => ObjClipboard.CopyAsync(tab, cut: false)), has, Key.C),
            Item(Strings.Menu_Paste, () => _ = SafeAsync(() => PasteToPageAsync(st, info)), canPaste, Key.V),
            Item(Strings.Menu_Duplicate, () => _ = SafeAsync(() => DuplicateSelectionAsync(st)), has, Key.D),
            new Separator(),
            del,
        };
        new ContextMenu { ItemsSource = items }.Open(this);
    }

    private void DeleteSelectionFromMenu(ViewState st)
    {
        var tab = st.Tab;
        if (tab.Objects.Selected is { } sel && tab.CanEditContent)
        {
            var gone = tab.Objects.All.ToList();
            tab.Objects.Clear();
            _ = RunObjectEditAsync(st, sel.Page, gone.Select(x => (ContentEdit)new DeactivateEdit(st.Session.Content.NewId(), x.Ref)).ToArray());
        }
        if (tab.Selection.Page is { } page && tab.CanAnnotate)
        {
            var cmd = tab.Editing.DeleteSelected(st.Session.Annotations.Get(page), tab.Selection);
            if (cmd is not null)
            {
                tab.Selection.Clear();
                _ = RunAnnotationCommandAsync(st, cmd, page, null);
            }
        }
    }
}
