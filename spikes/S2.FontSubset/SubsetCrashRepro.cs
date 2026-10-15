namespace S2.FontSubset;

/// <summary>
/// 간헐적인 네이티브 크래시 재현용. hb_subset_or_fail에서 0xC0000005(접근 위반)가 난다.
/// 2026-10-15 측정: 같은 서브셋을 400회 반복하면 8번 중 약 5번 호스트가 죽는다.
/// HarfBuzzSharp 8.3.1.3, 14.2.1.301 모두 같음 → 라이브러리 버전 문제가 아니라
/// FontSubsetter(P/Invoke 사용 방식) 쪽 문제일 가능성이 높다 (원인 미확정, 검증 필요).
/// 원인을 찾을 때 Skip을 풀어서 쓴다. 평소에는 전체 테스트가 죽지 않도록 건너뛴다.
/// </summary>
public class SubsetCrashRepro
{
    [Fact(Skip = "간헐 네이티브 크래시 재현용 (원인 조사 때만 실행)")]
    public void Subset_Repeated_DoesNotCrash()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        var font = File.ReadAllBytes(Path.Combine(dir!.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"));
        var cps = "ABC123".Select(c => (int)c).ToArray();
        for (int i = 0; i < 400; i++)
            Assert.NotEmpty(FontSubsetter.Subset(font, cps));
    }
}
