using Avalonia;
using PdfEditor.Document;

namespace PdfEditor.App.Viewer;

public enum TileKind { Tile, Thumbnail }

public sealed record TileRequest(TileKind Kind, TileKey Key, double Scale, PixelRect Rect);

public static class TilePriority
{
    /// <summary>보이는 타일(중심에서 가까운 순) → 화면 밖 한 타일 링 → 썸네일.</summary>
    /// <param name="docCenter">요청의 문서 좌표 중심. 생략하면 Rect(페이지 안 픽셀) 중심을 쓴다(한 페이지 안에서만 정확, 검증 필요: 여러 페이지는 호출 쪽에서 지정).</param>
    public static IReadOnlyList<TileRequest> Order(IEnumerable<TileRequest> visible, IEnumerable<TileRequest> ring,
        IEnumerable<TileRequest> thumbnails, Point viewportCenterDoc, Func<TileRequest, Point>? docCenter = null)
    {
        docCenter ??= r => new Point(r.Rect.X + r.Rect.W / 2.0, r.Rect.Y + r.Rect.H / 2.0);
        double Dist(TileRequest r)
        {
            var c = docCenter(r);
            double dx = c.X - viewportCenterDoc.X, dy = c.Y - viewportCenterDoc.Y;
            return dx * dx + dy * dy;
        }
        var res = new List<TileRequest>();
        res.AddRange(visible.OrderBy(Dist)); // OrderBy는 안정 정렬
        res.AddRange(ring);
        res.AddRange(thumbnails);
        return res;
    }
}

/// <summary>
/// 우선순위 목록을 받아 동시에 maxInFlight개까지만 렌더를 실행한다. PDFium 큐를 채우지 않기 위한 제한이다.
/// render가 null을 돌려주면(버전 불일치·페이지 삭제 등) onReady는 호출하지 않는다.
/// 인덱스는 render 안에서 PDFium 스레드 위에서 PageId로 다시 찾아야 한다(Review Focus 1).
/// onReady는 스레드 풀 스레드에서 불린다 — UI 쪽에서 디스패치할 것.
/// </summary>
public sealed class TileScheduler
{
    private readonly Func<TileRequest, CancellationToken, Task<byte[]?>> _render;
    private readonly Action<TileRequest, byte[]> _onReady;
    private readonly int _maxInFlight;
    private readonly object _lock = new();
    private readonly List<TileRequest> _pending = new();
    private readonly Dictionary<(TileKind, TileKey), int> _inFlight = new();
    private int _inFlightCount;
    private CancellationTokenSource _cts = new();

    public TileScheduler(Func<TileRequest, CancellationToken, Task<byte[]?>> render, Action<TileRequest, byte[]> onReady, int maxInFlight = 2)
    {
        _render = render;
        _onReady = onReady;
        _maxInFlight = Math.Max(1, maxInFlight);
    }

    public int PendingCount { get { lock (_lock) return _pending.Count; } }
    public int InFlightCount { get { lock (_lock) return _inFlightCount; } }

    public void SetWanted(IReadOnlyList<TileRequest> orderedByPriority)
    {
        lock (_lock)
        {
            _pending.Clear();
            var seen = new HashSet<(TileKind, TileKey)>();
            foreach (var r in orderedByPriority)
            {
                var id = (r.Kind, r.Key);
                if (_inFlight.ContainsKey(id) || !seen.Add(id)) continue;
                _pending.Add(r);
            }
        }
        Pump();
    }

    /// <summary>대기 목록을 비우고 실행 중인 요청에 취소를 요청한다. 결과는 버려진다.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _pending.Clear();
            _cts.Cancel();
            _cts = new CancellationTokenSource();
        }
    }

    private void Pump()
    {
        while (true)
        {
            TileRequest req; CancellationTokenSource cts;
            lock (_lock)
            {
                if (_inFlightCount >= _maxInFlight || _pending.Count == 0) return;
                req = _pending[0];
                _pending.RemoveAt(0);
                _inFlightCount++;
                var id = (req.Kind, req.Key);
                _inFlight[id] = _inFlight.GetValueOrDefault(id) + 1;
                cts = _cts;
            }
            _ = Task.Run(() => RunAsync(req, cts));
        }
    }

    private async Task RunAsync(TileRequest req, CancellationTokenSource cts)
    {
        byte[]? data = null;
        try { data = await _render(req, cts.Token).ConfigureAwait(false); }
        catch { data = null; } // 렌더 실패는 해당 요청만 버린다 (큐가 멈추지 않게)
        finally
        {
            lock (_lock)
            {
                _inFlightCount--;
                var id = (req.Kind, req.Key);
                if (_inFlight.TryGetValue(id, out var n)) { if (n <= 1) _inFlight.Remove(id); else _inFlight[id] = n - 1; }
            }
        }
        if (data is not null && !cts.IsCancellationRequested)
        {
            try { _onReady(req, data); } catch { /* 소비자 예외가 스케줄러를 멈추지 않게 */ }
        }
        Pump();
    }
}
