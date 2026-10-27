using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.VisualTree;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.App.Viewer;
using PdfEditor.App.Viewer.Annotations;
using PdfEditor.App.Viewer.Objects;

namespace PdfEditor.App.Tests.Viewer;

/// <summary>
/// 실제 Windows 한글 입력기의 키 순서를 흉내 낸 시험. 실제 입력기는 Enter/Esc에서 조합을 먼저 끝내고(PreeditText가 비워짐)
/// 그 키를 앱에 넘기므로, 키가 올 때 PreeditText가 이미 비어 있어도 "방금 조합이 끝났다"면 조합 몫이다.
/// </summary>
public class ImeCompositionGuardTests
{
    private static TextBox ShowBox(out Window w, out long[] now)
    {
        // 테마가 없으면 TextBox에 템플릿(TextPresenter)이 없다 — 다른 시험이 먼저 올려 주는 순서에 기대지 않는다(M5 마무리 검토).
        if (!Avalonia.Application.Current!.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>().Any())
            Avalonia.Application.Current.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        var t = new long[] { 1_000_000 };
        now = t;
        var box = new TextBox { AcceptsReturn = true, Text = "가나" };
        var win = new Window { Width = 300, Height = 200, Content = box };
        win.Show();
        win.UpdateLayout();
        ImeCompositionGuard.For(box).ClockOverride = () => t[0];
        w = win;
        return box;
    }

    private static TextPresenter Presenter(TextBox b) => b.GetVisualDescendants().OfType<TextPresenter>().First();

    private static KeyEventArgs Key(Key k, KeyModifiers m = KeyModifiers.None)
        => new() { RoutedEvent = InputElement.KeyDownEvent, Key = k, KeyModifiers = m };

    [Fact]
    public void Composing_EnterEsc_AreSwallowed()
    {
        HeadlessUi.Run(() =>
        {
            var box = ShowBox(out var w, out _);
            var g = ImeCompositionGuard.For(box);
            Presenter(box).PreeditText = "다";
            var enter = Key(Avalonia.Input.Key.Enter);
            var esc = Key(Avalonia.Input.Key.Escape);
            Assert.True(g.Consume(enter)); Assert.True(enter.Handled);
            Assert.True(g.Consume(esc)); Assert.True(esc.Handled);
            w.Close();
            return 0;
        });
    }

    [Fact]
    public void PreeditAlreadyEmpty_ButComposedJustBefore_EnterEscAreSwallowed()
    {
        HeadlessUi.Run(() =>
        {
            var box = ShowBox(out var w, out var now);
            var g = ImeCompositionGuard.For(box);
            Presenter(box).PreeditText = "나";
            Presenter(box).PreeditText = null;   // 입력기가 Enter 전에 조합을 먼저 끝냄
            now[0] += 5;                          // 같은 프레임(수 ms 뒤)에 Enter가 옴
            var enter = Key(Avalonia.Input.Key.Enter);
            Assert.True(g.Consume(enter));
            Assert.True(enter.Handled);           // 줄바꿈이 들어가지 않는다
            var esc = Key(Avalonia.Input.Key.Escape);
            Assert.True(g.Consume(esc));
            Assert.True(esc.Handled);
            w.Close();
            return 0;
        });
    }

    [Fact]
    public void AfterSettle_PlainEnterEscPassThrough()
    {
        HeadlessUi.Run(() =>
        {
            var box = ShowBox(out var w, out var now);
            var g = ImeCompositionGuard.For(box);
            Presenter(box).PreeditText = "나";
            Presenter(box).PreeditText = null;
            now[0] += ImeCompositionGuard.SettleMs + 1;
            var enter = Key(Avalonia.Input.Key.Enter);
            Assert.False(g.Consume(enter));
            Assert.False(enter.Handled);
            Assert.False(g.Consume(Key(Avalonia.Input.Key.Escape)));
            w.Close();
            return 0;
        });
    }

    [Fact]
    public void ImeProcessedKey_IsLeftToIme_AndCoversFollowingEnter()
    {
        HeadlessUi.Run(() =>
        {
            var box = ShowBox(out var w, out var now);
            var g = ImeCompositionGuard.For(box);
            var ime = Key(Avalonia.Input.Key.ImeProcessed);
            Assert.True(g.Consume(ime));
            Assert.False(ime.Handled);            // 입력기가 먹은 키는 건드리지 않는다
            now[0] += 3;
            var enter = Key(Avalonia.Input.Key.Enter);
            Assert.True(g.Consume(enter));        // 곧 이어 온 Enter도 조합 몫
            Assert.True(enter.Handled);
            now[0] += ImeCompositionGuard.SettleMs + 1;
            Assert.False(g.Consume(Key(Avalonia.Input.Key.Enter)));
            w.Close();
            return 0;
        });
    }

    [Fact]
    public void NoComposition_NothingIsSwallowed()
    {
        HeadlessUi.Run(() =>
        {
            var box = ShowBox(out var w, out _);
            var g = ImeCompositionGuard.For(box);
            Assert.False(g.Consume(Key(Avalonia.Input.Key.Enter)));
            Assert.False(g.Consume(Key(Avalonia.Input.Key.Escape)));
            w.Close();
            return 0;
        });
    }

    [Fact]
    public void ParagraphPopup_EscRightAfterCompositionEnds_DoesNotCancel_ThenEscCancels()
    {
        HeadlessUi.Run(() =>
        {
            var p = new ParagraphEditorPopup();
            var w = new Window { Width = 500, Height = 400, Content = p };
            w.Show();
            w.UpdateLayout();
            p.Load("가나", 14, 300, 60, canExtend: false);
            int cancels = 0, accepts = 0;
            p.CancelRequested += () => cancels++;
            p.AcceptRequested += () => accepts++;
            long t = 5_000_000;
            p.ImeGuard.ClockOverride = () => t;
            var box = p.FindControl<TextBox>("Box")!;
            var pres = box.GetVisualDescendants().OfType<TextPresenter>().First();

            pres.PreeditText = "다"; pres.PreeditText = null;   // 조합이 끝난 직후
            t += 4;
            var esc = Key(Avalonia.Input.Key.Escape);
            box.RaiseEvent(esc);
            Assert.Equal(0, cancels);                             // 조합만 끝나고 창은 그대로
            var enter = Key(Avalonia.Input.Key.Enter);
            t += 4;
            box.RaiseEvent(enter);
            Assert.True(enter.Handled);                           // 줄바꿈이 들어가지 않는다
            Assert.Equal("가나", p.Text);
            var ctrlEnter = Key(Avalonia.Input.Key.Enter, KeyModifiers.Control);
            t += 4;
            box.RaiseEvent(ctrlEnter);
            Assert.Equal(0, accepts);

            t += ImeCompositionGuard.SettleMs + 1;                // 조합이 끝난 지 오래된 뒤
            box.RaiseEvent(Key(Avalonia.Input.Key.Escape));
            Assert.Equal(1, cancels);                             // 이때만 취소
            box.RaiseEvent(Key(Avalonia.Input.Key.Enter, KeyModifiers.Control));
            Assert.Equal(1, accepts);
            w.Close();
            return 0;
        });
    }

    [Fact]
    public void LineEditor_EnterRightAfterCompositionEnds_DoesNotAccept()
    {
        HeadlessUi.Run(() =>
        {
            if (!Avalonia.Application.Current!.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>().Any())
                Avalonia.Application.Current.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
            var ed = new FreeTextEditor();
            var w = new Window { Width = 400, Height = 200, Content = ed };
            w.Show();
            ed.LoadLine("가나", 14, 100, Avalonia.Media.Colors.Black);
            w.UpdateLayout();
            int accepts = 0, cancels = 0;
            ed.AcceptRequested += () => accepts++;
            ed.CancelRequested += () => cancels++;
            long t = 9_000_000;
            ed.ImeGuard.ClockOverride = () => t;
            var box = ed.FindControl<TextBox>("Box")!;
            var pres = box.GetVisualDescendants().OfType<TextPresenter>().First();

            pres.PreeditText = "다"; pres.PreeditText = null;
            t += 4;
            box.RaiseEvent(Key(Avalonia.Input.Key.Enter));
            box.RaiseEvent(Key(Avalonia.Input.Key.Escape));
            Assert.Equal(0, accepts);
            Assert.Equal(0, cancels);
            t += ImeCompositionGuard.SettleMs + 1;
            box.RaiseEvent(Key(Avalonia.Input.Key.Enter));
            Assert.Equal(1, accepts);
            box.RaiseEvent(Key(Avalonia.Input.Key.Escape));
            Assert.Equal(1, cancels);
            w.Close();
            return 0;
        });
    }
}
