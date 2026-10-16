using PdfEditor.Document;

namespace PdfEditor.App.Viewer;

/// <summary>바이트 예산 LRU 캐시. 스레드 안전. 제거되는 이미지마다 onEvict를 부른다(락 밖이 아니라 안에서 호출되므로 가볍게 유지).</summary>
public sealed class TileCache<TImage> where TImage : class
{
    // 160 MB(계획서 값)에서는 300쪽 벤치 작업 집합이 428~434 MB로 목표(400 MB 미만)를 넘었다.
    // 96 MB로 줄여도 300쪽 스크롤 회색 비율은 세 방식 모두 0 %였다(M1 마무리 검토, 결과 문서 참고).
    public const long DefaultBudgetBytes = 96L * 1024 * 1024;

    private sealed record Entry(TileKey Key, TImage Image, long Bytes);

    private readonly object _lock = new();
    private readonly long _budget;
    private readonly Action<TImage> _onEvict;
    private readonly Dictionary<TileKey, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _lru = new(); // First = 가장 최근

    public TileCache(long budgetBytes, Action<TImage> onEvict)
    {
        _budget = budgetBytes;
        _onEvict = onEvict;
    }

    public long UsedBytes { get { lock (_lock) return _used; } }
    private long _used;

    public bool TryGet(TileKey key, out TImage image)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node); _lru.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }
        image = null!;
        return false;
    }

    public void Add(TileKey key, TImage image, long bytes)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var old)) RemoveNode(old);
            // 같은 자리의 더 낮은 AnnotRevision 항목은 새 것이 대신한다.
            foreach (var stale in _map.Where(kv => kv.Key.AnnotRevision < key.AnnotRevision && SamePosition(kv.Key, key)).Select(kv => kv.Value).ToList())
                RemoveNode(stale);
            var node = _lru.AddFirst(new Entry(key, image, bytes));
            _map[key] = node;
            _used += bytes;
            while (_used > _budget && _lru.Count > 1) RemoveNode(_lru.Last!);
        }
    }

    private static bool SamePosition(TileKey a, TileKey b) =>
        a.Page == b.Page && a.Version == b.Version && a.ScaleKey == b.ScaleKey && a.X == b.X && a.Y == b.Y;

    /// <summary>key와 같은 자리(페이지·버전·배율·X·Y)에서 key.AnnotRevision 이하 중 가장 높은 AnnotRevision의 이미지 (LRU 갱신 안 함).</summary>
    public bool TryGetAtOrBelowRevision(TileKey key, out TImage image)
    {
        lock (_lock)
        {
            LinkedListNode<Entry>? best = null;
            foreach (var kv in _map)
                if (kv.Key.AnnotRevision <= key.AnnotRevision && SamePosition(kv.Key, key)
                    && (best is null || kv.Key.AnnotRevision > best.Value.Key.AnnotRevision)) best = kv.Value;
            if (best is not null) { image = best.Value.Image; return true; }
        }
        image = null!;
        return false;
    }

    /// <summary>
    /// 대체 그림. ① 같은 배율에 이전 AnnotRevision 타일이 있으면 그것들(모양이 같아 그대로 그린다, 낮은 번호부터).
    /// ② 없으면 같은 AnnotRevision에서 scaleKey가 아닌 배율 중 |log(s/target)|이 가장 작은 배율의 타일 전부. (LRU 갱신 안 함)
    /// </summary>
    public IReadOnlyList<(TileKey Key, TImage Image)> FallbackFor(PageId page, int version, int annotRevision, int scaleKey)
    {
        lock (_lock)
        {
            var older = _map.Where(kv => kv.Key.Page == page && kv.Key.Version == version && kv.Key.ScaleKey == scaleKey
                                         && kv.Key.AnnotRevision < annotRevision)
                            .OrderBy(kv => kv.Key.AnnotRevision)
                            .Select(kv => (kv.Key, kv.Value.Value.Image)).ToList();
            if (older.Count > 0) return older;

            int bestScale = -1; double bestD = double.MaxValue;
            foreach (var k in _map.Keys)
            {
                if (k.Page != page || k.Version != version || k.AnnotRevision != annotRevision || k.ScaleKey == scaleKey || k.ScaleKey <= 0) continue;
                double d = Math.Abs(Math.Log((double)k.ScaleKey / scaleKey));
                if (d < bestD) { bestD = d; bestScale = k.ScaleKey; }
            }
            if (bestScale < 0) return Array.Empty<(TileKey, TImage)>();
            return _map.Where(kv => kv.Key.Page == page && kv.Key.Version == version && kv.Key.AnnotRevision == annotRevision && kv.Key.ScaleKey == bestScale)
                       .Select(kv => (kv.Key, kv.Value.Value.Image)).ToList();
        }
    }

    /// <summary>같은 페이지·버전의 저해상도 미리보기(ScaleKey가 음수) 전부 (LRU 갱신 안 함).</summary>
    public IReadOnlyList<(TileKey Key, TImage Image)> PreviewsFor(PageId page, int version)
    {
        lock (_lock)
        {
            return _map.Where(kv => kv.Key.Page == page && kv.Key.Version == version && kv.Key.IsPreview)
                       .OrderBy(kv => kv.Key.AnnotRevision)
                       .Select(kv => (kv.Key, kv.Value.Value.Image)).ToList();
        }
    }

    public void RemovePage(PageId page) => RemoveWhere(k => k.Page == page);

    /// <summary>currentVersion이 아닌 버전의 타일을 모두 지운다 (되돌리기로 번호가 내려갈 수도 있어 != 로 비교).</summary>
    public void RemoveStaleVersions(PageId page, int currentVersion) => RemoveWhere(k => k.Page == page && k.Version != currentVersion);

    private void RemoveWhere(Func<TileKey, bool> pred)
    {
        lock (_lock)
        {
            foreach (var node in _map.Where(kv => pred(kv.Key)).Select(kv => kv.Value).ToList()) RemoveNode(node);
        }
    }

    private void RemoveNode(LinkedListNode<Entry> node)
    {
        _lru.Remove(node);
        _map.Remove(node.Value.Key);
        _used -= node.Value.Bytes;
        _onEvict(node.Value.Image);
    }
}
