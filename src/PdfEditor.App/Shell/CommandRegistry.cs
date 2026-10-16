using System.Windows.Input;
using Avalonia.Input;

namespace PdfEditor.App.Shell;

/// <summary>앱 명령 하나. Label은 빠른 실행에서 검색되는 전체 이름이다.</summary>
public sealed record AppCommand(string Id, string Label, ICommand Command, KeyGesture? Gesture, string? IconKey, string? DisabledTip = null);

/// <summary>명령 목록과 빠른 실행(Ctrl+.) 검색.</summary>
public sealed class CommandRegistry
{
    private readonly List<AppCommand> _all = [];
    private readonly Dictionary<string, AppCommand> _byId = new(StringComparer.Ordinal);

    public IReadOnlyList<AppCommand> All => _all;

    public void Register(AppCommand c)
    {
        if (!_byId.TryAdd(c.Id, c)) throw new ArgumentException($"Duplicate command id: {c.Id}", nameof(c));
        _all.Add(c);
    }

    public AppCommand? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Label에 대소문자 무시 부분 일치. 빈 문자열이면 빈 목록. 실행 못 하는 명령은 뒤로(등록 순서 유지).</summary>
    public IReadOnlyList<AppCommand> Search(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var hits = _all.Where(c => c.Label.Contains(text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();
        return hits.Where(c => c.Command.CanExecute(null))
            .Concat(hits.Where(c => !c.Command.CanExecute(null))).ToList();
    }
}
