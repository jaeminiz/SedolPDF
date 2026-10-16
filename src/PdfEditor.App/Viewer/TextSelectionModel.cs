using PdfEditor.Document;

namespace PdfEditor.App.Viewer;

/// <summary>
/// 텍스트 선택 상태. 한 쪽 안에서만 선택한다(여러 쪽 선택은 M1 범위 밖).
/// 끌기 시작 글자(anchor)와 현재 글자(focus)를 기억하고, Range는 항상 앞에서부터의 (시작, 글자 수)이다.
/// </summary>
public sealed class TextSelectionModel
{
    private int _anchor = -1, _focus = -1;

    public PageId? Page { get; private set; }

    /// <summary>선택이 바뀔 때마다 발생(UI 스레드).</summary>
    public event Action? Changed;

    public void Begin(PageId page, int charIndex)
    {
        Page = page;
        _anchor = _focus = Math.Max(0, charIndex);
        Changed?.Invoke();
    }

    public void Extend(int charIndex)
    {
        if (Page is null) return;
        _focus = Math.Max(0, charIndex);
        Changed?.Invoke();
    }

    public (int Start, int Count)? Range =>
        Page is null ? null : (Math.Min(_anchor, _focus), Math.Abs(_focus - _anchor) + 1);

    public void Clear()
    {
        Page = null;
        _anchor = _focus = -1;
        Changed?.Invoke();
    }
}

/// <summary>클립보드를 추상화해서 테스트에서 가짜로 바꿀 수 있게 한다. 실제 구현은 DocumentView가 Avalonia 클립보드로 만든다.</summary>
public interface IClipboardService
{
    Task SetTextAsync(string text);
}

public enum CopyResult { Copied, Restricted, NothingSelected }

public static class TextCopier
{
    /// <summary>선택한 글자를 클립보드에 넣는다. 문서 권한이 복사를 막으면 클립보드를 건드리지 않는다.</summary>
    public static async Task<CopyResult> CopyAsync(DocumentSession session, TextSelectionModel selection, IClipboardService clipboard)
    {
        if (selection.Page is not { } page || selection.Range is not { } r) return CopyResult.NothingSelected;
        if (!session.Permissions.CanCopy) return CopyResult.Restricted;
        string text = await session.Thread.InvokeAsync(() =>
            session.IndexOf(page) < 0 ? "" : session.GetLoadedText(page).GetText(r.Start, r.Count));
        if (text.Length == 0) return CopyResult.NothingSelected;
        await clipboard.SetTextAsync(text);
        return CopyResult.Copied;
    }
}
