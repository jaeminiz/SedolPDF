using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.Document;
using RectangleF = System.Drawing.RectangleF;

namespace PdfEditor.App.Viewer;

// 글 선택·손 도구에서 오른쪽 버튼을 눌렀을 때의 상황 메뉴와 모두 선택(Ctrl+A).
public sealed partial class DocumentView
{
    private static bool UsesTextMenu(ToolKind t) => t is ToolKind.TextSelect or ToolKind.Hand;

    /// <summary>쪽 전체를 덮는 사각형 선택. 사각형은 쪽 좌표라 아주 크게 잡으면 쪽 전체가 된다(테두리는 끌 때만 보인다).</summary>
    private void SelectAllOnPage(int pageIndex)
    {
        if (_state is not { } st || pageIndex < 0 || pageIndex >= _layoutPages.Length) return;
        st.Tab.TextSelection.BeginRect(_layoutPages[pageIndex].Id, new System.Drawing.PointF(-100000, -100000));
        st.Tab.TextSelection.ExtendRect(new System.Drawing.PointF(100000, 100000));
    }

    /// <summary>Ctrl+A: 선택이 있는 쪽, 없으면 화면 가운데에 보이는 쪽.</summary>
    private void SelectAllFromKeyboard()
    {
        if (_state is not { } st || _layout is not { } lay) return;
        int idx = -1;
        if (st.Tab.TextSelection.Page is { } sp)
            for (int i = 0; i < _layoutPages.Length; i++) if (_layoutPages[i].Id == sp) { idx = i; break; }
        if (idx < 0 && lay.HitTest(Viewport.Center) is { } h) idx = h.Page;
        SelectAllOnPage(idx);
    }

    private void ShowTextMenu(PointerPressedEventArgs e)
    {
        if (_state is not { } st) return;
        var tab = st.Tab;
        int pageIdx = _layout?.HitTest(e.GetPosition(this))?.Page ?? -1;
        bool hasSel = tab.TextSelection.Page is not null && (tab.TextSelection.RectPt is not null || tab.TextSelection.Range is not null);
        bool canCopy = hasSel && st.Session.Permissions.CanCopy;
        bool canMark = hasSel && tab.CanAnnotate;

        MenuItem Item(string header, Action act, bool enabled = true, KeyGesture? gesture = null)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled, InputGesture = gesture };
            mi.Click += (_, _) => act();
            return mi;
        }
        MenuItem ToolItem(string header, ToolKind tool)
        {
            var mi = Item(header, () => tab.Tool = tool);
            mi.ToggleType = MenuItemToggleType.Radio;
            mi.IsChecked = tab.Tool == tool;
            return mi;
        }

        var items = new List<Control>();
        if (hasSel)
        {
            items.Add(Item(Strings.Ctx_Copy, CopySelection, canCopy, new KeyGesture(Key.C, KeyModifiers.Control)));
            items.Add(Item(Strings.Ctx_SelectAll, () => SelectAllOnPage(pageIdx >= 0 ? pageIdx : 0), pageIdx >= 0, new KeyGesture(Key.A, KeyModifiers.Control)));
            items.Add(new Separator());
            items.Add(Item(Strings.Ctx_Highlight, () => _ = MarkupSelectionAsync(st, ToolKind.HighlightText), canMark));
            items.Add(Item(Strings.Ctx_Underline, () => _ = MarkupSelectionAsync(st, ToolKind.Underline), canMark));
            items.Add(Item(Strings.Ctx_Strikeout, () => _ = MarkupSelectionAsync(st, ToolKind.StrikeOut), canMark));
            items.Add(new Separator());
            items.Add(Item(Strings.Ctx_FindSelected, () => _ = FindSelectionAsync(st), canCopy));
            items.Add(new Separator());
        }
        else
        {
            items.Add(Item(Strings.Ctx_SelectAll, () => SelectAllOnPage(pageIdx), pageIdx >= 0, new KeyGesture(Key.A, KeyModifiers.Control)));
            items.Add(new Separator());
        }
        items.Add(ToolItem(Strings.Ctx_TextSelectTool, ToolKind.TextSelect));
        items.Add(ToolItem(Strings.Ctx_HandTool, ToolKind.Hand));
        new ContextMenu { ItemsSource = items }.Open(this);
    }

    private async Task FindSelectionAsync(ViewState st)
    {
        try
        {
            string text = await TextCopier.ReadTextAsync(st.Session, st.Tab.TextSelection);
            string first = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
            if (first.Length > 100) first = first[..100];
            if (first.Length > 0 && !st.Disposed) st.Tab.RequestFind(first);
        }
        catch (Exception) { }   // PDFium 스레드가 닫혔다
    }

    /// <summary>이미 잡아 둔 선택에 텍스트 마크업 주석(강조·밑줄·취소선)을 단다. 끌어서 만들 때와 같은 명령을 쓴다.</summary>
    private async Task MarkupSelectionAsync(ViewState st, ToolKind tool)
    {
        var tab = st.Tab;
        var sel = tab.TextSelection;
        if (st.Disposed || sel.Page is not { } page || !tab.CanAnnotate) return;
        var rectPt = sel.RectPt;
        var range = sel.Range;
        var session = st.Session;
        try
        {
            var rects = await session.Thread.InvokeAsync(() =>
                session.IndexOf(page) < 0 ? Array.Empty<RectangleF>()
                : rectPt is { } rc ? [.. RectTextSelector.Resolve(session, page, rc).Rects]
                : range is { } r ? [.. session.GetLoadedText(page).GetRects(r.Start, r.Count)]
                : Array.Empty<RectangleF>());
            if (st.Disposed || rects.Length == 0) return;
            var cmd = tab.Tools.CreateMarkup(tool, DocumentTabViewModel.StyleFor(tool), page, [.. rects.Select(Pdf.Native.PdfRectF.FromLtrbRectangle)]);
            sel.Clear();
            if (cmd is not null) await RunAnnotationCommandAsync(st, cmd, page, null);
        }
        catch (Exception) { }   // PDFium 스레드가 닫혔다
    }
}
