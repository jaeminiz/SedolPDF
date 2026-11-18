using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using PdfEditor.App.Dialogs;
using PdfEditor.App.Documents;
using PdfEditor.App.Tests.Viewer.Annotations;
using Xunit;

namespace PdfEditor.App.Tests.Dialogs;

/// <summary>변경 내용 저장 질문의 단축 글쇠(예(Y)/모두 저장(E)/저장 안 함(N)/모두 저장 안 함(L)/취소(C)).</summary>
public class SaveChangesPromptKeyTests
{
    private static UnsavedChoice Ask(bool offerAll, Key key, RawInputModifiers mods = RawInputModifiers.None, bool expectPending = false)
        => HeadlessUi.Run(() =>
        {
            var owner = new Window();
            owner.Show();
            var prompts = new AvaloniaPrompts(owner);
            var task = prompts.AskSaveChangesAsync("a.pdf", offerAll);
            Dispatcher.UIThread.RunJobs();
            var dlg = Assert.Single(owner.OwnedWindows);
            dlg.KeyPress(key, mods, ToPhysical(key), null);
            Dispatcher.UIThread.RunJobs();
            if (expectPending)
            {
                Assert.False(task.IsCompleted);
                dlg.Close();
                Dispatcher.UIThread.RunJobs();
            }
            var r = task.IsCompleted ? task.Result : UnsavedChoice.Cancel;
            owner.Close();
            return r;
        });

    private static PhysicalKey ToPhysical(Key k) => k switch
    {
        Key.Y => PhysicalKey.Y, Key.N => PhysicalKey.N, Key.C => PhysicalKey.C, Key.E => PhysicalKey.E, Key.L => PhysicalKey.L,
        Key.Escape => PhysicalKey.Escape, Key.Enter => PhysicalKey.Enter, _ => PhysicalKey.None,
    };

    [Theory]
    [InlineData(Key.Y, UnsavedChoice.Save)]
    [InlineData(Key.N, UnsavedChoice.Discard)]
    [InlineData(Key.C, UnsavedChoice.Cancel)]
    [InlineData(Key.Escape, UnsavedChoice.Cancel)]
    [InlineData(Key.Enter, UnsavedChoice.Save)]
    public void Single_Keys_ReturnChoice(Key key, UnsavedChoice expected)
        => Assert.Equal(expected, Ask(false, key));

    [Theory]
    [InlineData(Key.Y, UnsavedChoice.Save)]
    [InlineData(Key.N, UnsavedChoice.Discard)]
    [InlineData(Key.C, UnsavedChoice.Cancel)]
    public void Single_AltPlusKey_Works(Key key, UnsavedChoice expected)
        => Assert.Equal(expected, Ask(false, key, RawInputModifiers.Alt));

    [Theory]
    [InlineData(Key.E)]
    [InlineData(Key.L)]
    public void Single_AllKeys_DoNothing(Key key)
    {
        // 모두 단추가 없는 질문에서는 E·L이 아무것도 하지 않는다(창이 그대로 열려 있다).
        Ask(false, key, expectPending: true);
    }

    [Theory]
    [InlineData(Key.Y, UnsavedChoice.Save)]
    [InlineData(Key.E, UnsavedChoice.SaveAll)]
    [InlineData(Key.N, UnsavedChoice.Discard)]
    [InlineData(Key.L, UnsavedChoice.DiscardAll)]
    [InlineData(Key.C, UnsavedChoice.Cancel)]
    public void Multi_Keys_ReturnChoice(Key key, UnsavedChoice expected)
        => Assert.Equal(expected, Ask(true, key));

    [Fact]
    public void CtrlPlusKey_IsIgnored() => Ask(false, Key.Y, RawInputModifiers.Control, expectPending: true);

    [Fact]
    public void Labels_ShowKeys_AndKeysDoNotFireInTextBox()
    {
        HeadlessUi.Run(() =>
        {
            var dlg = new MessageDialog("t", "m", ["저장(Y)", "취소(C)"], 0, [Key.Y, Key.C]);
            dlg.Show();
            Assert.Equal(["저장(Y)", "취소(C)"], dlg.GetVisualDescendantsOfType<Button>().Select(b => (string)b.Content!).ToArray());
            // 글 상자에 초점이 있으면 글쇠로 단추가 눌리지 않는다.
            var box = new TextBox();
            ((StackPanel)dlg.Content!).Children.Add(box);
            Dispatcher.UIThread.RunJobs();
            box.Focus();
            dlg.KeyPress(Key.Y, RawInputModifiers.None, PhysicalKey.Y, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(-1, dlg.Result);
            dlg.Close();
            return 0;
        });
    }
}
