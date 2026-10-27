using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace PdfEditor.App.Viewer;

/// <summary>
/// 입력칸(TextBox)의 한글 IME 조합 판정을 한 곳에 모은 도우미 (한 줄 편집 창·문단 편집 창·답글 칸이 같이 쓴다).
/// 실제 Windows 한글 입력기는 Enter/Esc에서 조합을 먼저 끝내고(PreeditText가 비워짐) 그 키를 앱에 그대로 넘기므로,
/// 키가 올 때 "지금 조합 중인가"만 보면 놓친다. 그래서 (1) ImeProcessed 키, (2) 지금 조합 중인 글자가 있음,
/// (3) 조합이 방금(SettleMs 안에) 끝남 — 셋 중 하나면 그 키는 조합을 끝내는 키로 보고, Enter/Esc는 삼킨다(Handled).
/// </summary>
public sealed class ImeCompositionGuard
{
    /// <summary>조합이 끝난 뒤 이 시간(ms) 안에 오는 Enter/Esc는 조합을 끝낸 같은 키 입력으로 본다.</summary>
    public const int SettleMs = 150;

    /// <summary>시계(ms). 시험에서 바꾼다.</summary>
    public static Func<long> Clock { get; set; } = () => Environment.TickCount64;

    private static readonly ConditionalWeakTable<TextBox, ImeCompositionGuard> Map = new();

    private readonly TextBox _box;
    private TextPresenter? _presenter;
    private bool _wasComposing;
    private long _endedAt = long.MinValue / 2;

    private ImeCompositionGuard(TextBox box)
    {
        _box = box;
        box.TemplateApplied += (_, _) => Hook();
        Hook();
    }

    /// <summary>시험용: 이 입력칸만 쓰는 시계(ms). 비우면 Clock.</summary>
    public Func<long>? ClockOverride { get; set; }

    private long Now() => (ClockOverride ?? Clock)();

    public static ImeCompositionGuard For(TextBox box) => Map.GetValue(box, b => new ImeCompositionGuard(b));

    private void Hook()
    {
        var p = _box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        if (p is null || ReferenceEquals(p, _presenter)) return;
        if (_presenter is not null) _presenter.PropertyChanged -= OnPresenterChanged;
        _presenter = p;
        _wasComposing = !string.IsNullOrEmpty(p.PreeditText);
        p.PropertyChanged += OnPresenterChanged;
    }

    private void OnPresenterChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TextPresenter.PreeditTextProperty) return;
        bool now = !string.IsNullOrEmpty(_presenter?.PreeditText);
        if (_wasComposing && !now) _endedAt = Now();
        _wasComposing = now;
    }

    /// <summary>지금 조합 중인 글자(밑줄 표시 중인 글)가 있는가.</summary>
    public bool IsComposing
    {
        get
        {
            Hook();
            return !string.IsNullOrEmpty(_presenter?.PreeditText);
        }
    }

    /// <summary>조합이 방금 끝났는가 (시험용 공개).</summary>
    public bool EndedJustNow => Now() - _endedAt < SettleMs;

    /// <summary>시험용: "방금 끝남" 기억을 지운다 (시간이 충분히 흐른 것과 같다).</summary>
    public void ForgetRecentEnd() => _endedAt = long.MinValue / 2;

    /// <summary>
    /// 이 KeyDown이 입력기 몫인가. 그렇다면 true — 부른 쪽은 확정·취소·줄바꿈 처리를 하지 않고 돌아간다.
    /// ImeProcessed가 아닌 Enter/Esc는 Handled로 막아 입력칸이 줄바꿈 등을 넣지 않게 한다.
    /// </summary>
    public bool Consume(KeyEventArgs e)
    {
        Hook();
        if (e.Key == Key.ImeProcessed)
        {
            _endedAt = Now();   // 입력기가 먹은 키 — 곧 이어 오는 같은 키 입력도 조합 몫
            return true;
        }
        if (!IsComposing && !EndedJustNow) return false;
        if (e.Key is Key.Enter or Key.Escape) e.Handled = true;
        return true;
    }
}
