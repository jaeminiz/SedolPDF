using PdfEditor.Document;

namespace PdfEditor.App.Hosting;

/// <summary>열린 창 목록. 앞일수록 최근에 활성화된 창이다. UI 스레드에서만 쓴다.</summary>
public sealed class WindowRegistry
{
    private readonly List<IHostWindow> _windows = [];

    /// <summary>새 창은 가장 최근 창이 된다.</summary>
    public void Add(IHostWindow w)
    {
        _windows.Remove(w);
        _windows.Insert(0, w);
    }

    public void Remove(IHostWindow w) => _windows.Remove(w);

    /// <summary>창이 활성화되면 맨 앞으로. 목록에 없는 창은 무시한다.</summary>
    public void MarkActivated(IHostWindow w)
    {
        if (!_windows.Remove(w)) return;
        _windows.Insert(0, w);
    }

    /// <summary>닫히지 않은 창, 최근 먼저.</summary>
    public IReadOnlyList<IHostWindow> ByRecent => _windows.Where(w => !w.IsClosed).ToList();

    public IHostWindow? LastActive => _windows.FirstOrDefault(w => !w.IsClosed);

    public int Count => _windows.Count(w => !w.IsClosed);

    /// <summary>그 세션을 탭으로 가진 창. 없으면 null.</summary>
    public IHostWindow? FindOwner(DocumentSession s) =>
        _windows.FirstOrDefault(w => !w.IsClosed && w.ViewModel.Documents.Any(d => ReferenceEquals(d.Session, s)));
}
