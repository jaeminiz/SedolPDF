namespace PdfEditor.Optimize;

/// <summary>한 단계의 이미지 설정: 이 dpi를 넘는 이미지는 줄이고, JPEG는 이 품질(0~100)로 다시 인코딩한다.</summary>
public readonly record struct LadderLevel(int MaxDpi, int JpegQuality);

/// <summary>
/// <see cref="SizeLadder.FirstFitting"/>의 결과. Level: 목표 이하인 가장 약한(번호가 가장 작은) 단계, 없으면 null.
/// BytesAtLevel: 그 단계의 크기(Level이 null이면 가장 센 단계의 크기와 같다). BytesAtStrongest: 가장 센 단계의 크기.
/// </summary>
public sealed record LadderSearch(int? Level, long BytesAtLevel, long BytesAtStrongest);

/// <summary>용량 줄이기 단계표(스펙 2026-11-05)와 "목표 이하인 가장 약한 단계" 이진 탐색.</summary>
public static class SizeLadder
{
    /// <summary>이미지 재압축 단계. 번호가 클수록 세다(0 = 가장 약함).</summary>
    public static IReadOnlyList<LadderLevel> Image { get; } =
        [new(300, 85), new(200, 80), new(150, 70), new(120, 60), new(96, 45)];

    /// <summary>래스터화(쪽을 이미지로 바꿈) 단계. 쪽 전체를 이 dpi로 그린다. 글자가 읽히려면 120dpi 밑으로는 내리지 않는다.</summary>
    public static IReadOnlyList<LadderLevel> Raster { get; } =
        [new(200, 75), new(150, 65), new(120, 55)];

    /// <summary>
    /// 단계가 클수록 결과가 작아진다고 보고, 목표 이하인 가장 약한 단계를 찾는다. measureBytes(i)는 단계 i를 실제로 적용한 결과 크기이며
    /// 같은 단계는 한 번만 잰다. 먼저 가장 센 단계를 재고(안 맞으면 끝), 그 위에서 이진 탐색한다.
    /// </summary>
    public static LadderSearch FirstFitting(int levelCount, Func<int, long> measureBytes, long targetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(levelCount, 1);
        var cache = new Dictionary<int, long>();
        long Measure(int i)
        {
            if (!cache.TryGetValue(i, out long v)) cache[i] = v = measureBytes(i);
            return v;
        }

        int last = levelCount - 1;
        long strongest = Measure(last);
        if (strongest > targetBytes) return new LadderSearch(null, strongest, strongest);

        int lo = 0, hi = last;   // hi는 항상 맞는 단계
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Measure(mid) <= targetBytes) hi = mid; else lo = mid + 1;
        }
        return new LadderSearch(lo, Measure(lo), strongest);
    }
}
