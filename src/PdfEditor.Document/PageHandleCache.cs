using Pdf.Native;

namespace PdfEditor.Document;

/// <summary>
/// 열어 둔 PdfPage(최대 8개)·PdfTextPage(최대 4개)를 LRU로 재사용한다. PDFium 전용 스레드에서만 쓴다.
/// 밀려난 핸들은 바로 닫히므로, 호출자는 받은 핸들을 다음 호출 너머로 들고 있으면 안 된다.
/// </summary>
internal sealed class PageHandleCache
{
    private readonly int _pageCap, _textCap;
    private readonly LinkedList<PageId> _pageOrder = new();
    private readonly Dictionary<PageId, (PdfPage Page, LinkedListNode<PageId> Node)> _pages = new();
    private readonly LinkedList<PageId> _textOrder = new();
    private readonly Dictionary<PageId, (PdfTextPage Text, LinkedListNode<PageId> Node)> _texts = new();

    public PageHandleCache(int pageCapacity = 8, int textCapacity = 4)
    {
        _pageCap = pageCapacity;
        _textCap = textCapacity;
    }

    public PdfPage GetPage(PageId id, Func<PdfPage> load)
    {
        if (_pages.TryGetValue(id, out var e))
        {
            _pageOrder.Remove(e.Node);
            _pageOrder.AddFirst(e.Node);
            return e.Page;
        }
        var page = load();
        _pages[id] = (page, _pageOrder.AddFirst(id));
        while (_pages.Count > _pageCap)
            Evict(_pageOrder.Last!.Value);
        return page;
    }

    public PdfTextPage GetText(PageId id, Func<PdfPage> loadPage)
    {
        if (_texts.TryGetValue(id, out var e))
        {
            _textOrder.Remove(e.Node);
            _textOrder.AddFirst(e.Node);
            return e.Text;
        }
        var text = GetPage(id, loadPage).LoadText();
        _texts[id] = (text, _textOrder.AddFirst(id));
        while (_texts.Count > _textCap)
            EvictText(_textOrder.Last!.Value);
        return text;
    }

    /// <summary>한 페이지의 핸들만 닫는다 (페이지가 바뀌었거나 지워졌을 때).</summary>
    public void Evict(PageId id)
    {
        // 텍스트 핸들은 페이지 핸들보다 먼저 닫아야 한다 (텍스트가 페이지 객체를 참조)
        EvictText(id);
        if (_pages.Remove(id, out var e))
        {
            _pageOrder.Remove(e.Node);
            e.Page.Dispose();
        }
    }

    private void EvictText(PageId id)
    {
        if (_texts.Remove(id, out var e))
        {
            _textOrder.Remove(e.Node);
            e.Text.Dispose();
        }
    }

    public void Clear()
    {
        foreach (var id in _texts.Keys.ToArray()) EvictText(id);
        foreach (var id in _pages.Keys.ToArray()) Evict(id);
    }
}
