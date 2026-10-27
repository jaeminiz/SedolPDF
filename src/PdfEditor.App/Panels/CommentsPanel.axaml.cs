using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfEditor.App.Resources;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Panels;

/// <summary>
/// 코멘트 패널: 검색·거르기·정렬 + 가상화 목록. 줄을 누르면 그 위치로 이동하고, 줄 메뉴(⋯ 단추·오른쪽 단추)에서
/// 답글·상태·삭제·위치로 이동을 고른다. 답글 입력칸은 줄 아래에 펼쳐진다 (Enter 확정, Shift+Enter 줄바꿈, Esc 취소).
/// </summary>
public sealed partial class CommentsPanel : UserControl
{
    public CommentsPanel()
    {
        InitializeComponent();
        List.AddHandler(TappedEvent, OnTapped, RoutingStrategies.Bubble);
        List.AddHandler(Button.ClickEvent, OnButtonClick, RoutingStrategies.Bubble);
        List.AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private CommentsViewModel? Vm => DataContext as CommentsViewModel;

    private static CommentItem? ItemOf(object? source)
        => (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as CommentItem;

    private static bool InsideInput(object? source)
        => source is Visual v && (v.FindAncestorOfType<Button>(includeSelf: true) is not null || v.FindAncestorOfType<TextBox>(includeSelf: true) is not null);

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not { } vm || InsideInput(e.Source) || ItemOf(e.Source) is not { } item) return;
        vm.GoTo(item);
    }

    private void OnButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { Name: "RowMenuButton" } b && ItemOf(b) is { } item)
        {
            e.Handled = true;
            ShowMenu(b, item);
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is Visual v && ItemOf(v) is { } item && v.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } row)
        {
            e.Handled = true;
            ShowMenu(row, item);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox { Tag: "reply" } tb || tb.DataContext is not CommentItem item || Vm is not { } vm) return;
        // 한글 등 IME가 키를 먹었거나 조합 중인 글자가 있으면 Enter·Esc는 조합을 끝내는 키다 — 답글을 확정/취소하지 않는다(그다음 Enter가 확정).
        if (PdfEditor.App.Viewer.ImeCompositionGuard.For(tb).Consume(e)) return;
        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            e.Handled = true;   // Shift+Enter는 그대로 줄바꿈
            _ = vm.CommitReplyAsync(item, tb.Text ?? "");   // Draft 연결이 아직 못 받은 글이 없게 입력칸의 글을 직접 넘긴다
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.CancelReply(item);
        }
    }

    private void ShowMenu(Control target, CommentItem item)
    {
        if (Vm is not { } vm) return;
        string? whyNot = !item.Editable ? Strings.Comment_ReadOnlyTip : vm.ReplyNotice;
        bool canReply = vm.ReplyCommand.CanExecute(item);

        var reply = new MenuItem { Header = Strings.Comment_Reply, IsEnabled = canReply };
        reply.Click += (_, _) => { vm.ReplyCommand.Execute(item); FocusReplyBox(item); };
        var status = new MenuItem { Header = Strings.Comment_Status, IsEnabled = canReply };
        foreach (var s in Enum.GetValues<ReviewState>())
        {
            var state = s;
            var m = new MenuItem { Header = CommentsViewModel.StateName(s), ToggleType = MenuItemToggleType.Radio, IsChecked = item.State == s };
            m.Click += (_, _) => _ = vm.SetStateSafeAsync(item, state);
            status.Items.Add(m);
        }
        var delete = new MenuItem { Header = Strings.Comment_Delete, IsEnabled = vm.CanDelete(item) };
        delete.Click += (_, _) => vm.DeleteCommand.Execute(item);
        var goTo = new MenuItem { Header = Strings.Comment_GoTo };
        goTo.Click += (_, _) => vm.GoTo(item);

        if (whyNot is not null)
        {
            ToolTip.SetTip(reply, whyNot); ToolTip.SetShowOnDisabled(reply, true);
            ToolTip.SetTip(status, whyNot); ToolTip.SetShowOnDisabled(status, true);
        }
        var menu = new ContextMenu { ItemsSource = new Control[] { reply, status, new Separator(), delete, goTo } };
        menu.Open(target);
    }

    private void FocusReplyBox(CommentItem item)
        => Dispatcher.UIThread.Post(() =>
        {
            if (List.ContainerFromItem(item) is Control row)
                row.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "ReplyBox")?.Focus();
        }, DispatcherPriority.Background);
}
