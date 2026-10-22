using System.Drawing;
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
        RectPt = null;
        _anchor = _focus = Math.Max(0, charIndex);
        Changed?.Invoke();
    }

    public void Extend(int charIndex)
    {
        if (Page is null) return;
        _focus = Math.Max(0, charIndex);
        Changed?.Invoke();
    }

    // 사각형 선택(Alt+끌기): 쪽 좌표(pt)로 저장해서 확대·스크롤에도 그대로 유지된다.
    private PointF _rectAnchor, _rectFocus;

    /// <summary>사각형 선택 중이면 쪽 좌표 사각형(PDF 사용자 좌표: Y=아래, 높이는 위로). 읽는 순서 선택에서는 null.</summary>
    public RectangleF? RectPt { get; private set; }

    public void BeginRect(PageId page, PointF pagePt)
    {
        Page = page;
        _anchor = _focus = -1;
        _rectAnchor = _rectFocus = pagePt;
        RectPt = ToRect();
        Changed?.Invoke();
    }

    public void ExtendRect(PointF pagePt)
    {
        if (Page is null || RectPt is null) return;
        _rectFocus = pagePt;
        RectPt = ToRect();
        Changed?.Invoke();
    }

    private RectangleF ToRect() => RectangleF.FromLTRB(
        Math.Min(_rectAnchor.X, _rectFocus.X), Math.Min(_rectAnchor.Y, _rectFocus.Y),
        Math.Max(_rectAnchor.X, _rectFocus.X), Math.Max(_rectAnchor.Y, _rectFocus.Y));

    /// <summary>글자 범위 선택일 때만 값이 있다. 사각형 선택에서는 null(RectPt를 쓴다).</summary>
    public (int Start, int Count)? Range =>
        Page is null || RectPt is not null ? null : (Math.Min(_anchor, _focus), Math.Abs(_focus - _anchor) + 1);

    public void Clear()
    {
        Page = null;
        RectPt = null;
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
        if (selection.Page is null || (selection.RectPt is null && selection.Range is null)) return CopyResult.NothingSelected;
        if (!session.Permissions.CanCopy) return CopyResult.Restricted;
        string text = await ReadTextAsync(session, selection);
        if (text.Length == 0) return CopyResult.NothingSelected;
        await clipboard.SetTextAsync(text);
        return CopyResult.Copied;
    }

    /// <summary>선택한 글(읽는 순서). 선택이 없으면 빈 문자열. 권한은 확인하지 않는다(복사 전에 호출자가 확인).</summary>
    public static async Task<string> ReadTextAsync(DocumentSession session, TextSelectionModel selection)
    {
        if (selection.Page is not { } page) return "";
        var rectPt = selection.RectPt;
        var range = selection.Range;
        if (rectPt is null && range is null) return "";
        return await session.Thread.InvokeAsync(() =>
            rectPt is { } rc ? RectTextSelector.Resolve(session, page, rc).Result.Text
            : session.IndexOf(page) < 0 ? "" : session.GetLoadedText(page).GetText(range!.Value.Start, range.Value.Count));
    }
}
