using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PdfEditor.ContentStream;

/// <summary>
/// 0.14.0: "이 쪽 내용은 고칠 것이 없다"는 검사 결과를 내용 지문으로 기억한다(프로세스 전역, 스레드 안전).
/// 지문 = 스트림마다 (길이, SHA-256)을 차례로 이어 붙여 다시 SHA-256. 스트림 경계가 키에 들어가므로 경계만 다른 내용은 다른 키다(RepairStreams는 경계에 따라 결과가 달라진다).
/// 쪽 번호·PageId와 무관하므로 쪽 넣기·지우기·순서 바꾸기·다른 파일에서 가져온 쪽에도 맞는다. 깨끗하지 않은 쪽은 넣지 않는다.
/// </summary>
public static class ContentRepairCache
{
    private const int MaxEntries = 500_000;   // 쪽마다 지문 하나(약 100바이트): 넘치면 비우고 다시 쌓는다
    private static readonly ConcurrentDictionary<string, byte> Clean = new(StringComparer.Ordinal);
    private static long _hits;

    /// <summary>시험용: 지금까지 캐시 적중 수.</summary>
    public static long Hits => Interlocked.Read(ref _hits);

    public static int Count => Clean.Count;

    public static void Clear() => Clean.Clear();

    public static string Fingerprint(IReadOnlyList<byte[]> streams)
    {
        using var outer = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buf = stackalloc byte[8 + 32];
        foreach (var s in streams)
        {
            BitConverter.TryWriteBytes(buf[..8], (long)s.Length);
            SHA256.HashData(s, buf[8..]);
            outer.AppendData(buf);
        }
        return Convert.ToHexString(outer.GetHashAndReset()) + ":" + streams.Count;
    }

    public static bool IsClean(string key)
    {
        if (!Clean.ContainsKey(key)) return false;
        Interlocked.Increment(ref _hits);
        return true;
    }

    public static void MarkClean(string key)
    {
        if (Clean.Count >= MaxEntries) Clean.Clear();
        Clean[key] = 0;
    }
}
