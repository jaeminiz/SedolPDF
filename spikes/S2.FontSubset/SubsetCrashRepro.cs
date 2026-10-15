using System.Runtime;
using System.Runtime.InteropServices;

namespace S2.FontSubset;

/// <summary>
/// hb_subset_or_fail 간헐 접근 위반(0xC0000005)의 회귀 테스트.
/// 원인 (2026-10-15 확인): HarfBuzzSharp의 Blob.FromStream이 고정되지 않은 관리 배열을 가리키는 Blob을 만든다.
/// GC가 그 배열을 옮기거나 회수하면 HarfBuzz가 해제된 메모리를 읽는다.
/// Blob.FromStream으로 만든 Blob은 LOH 압축 뒤 데이터를 읽으면 매번 접근 위반이 났다 (CreateBlob은 통과).
/// FontSubsetter.CreateBlob(네이티브 메모리 복사)으로 고친 뒤에는 GC를 강제해도 죽지 않아야 한다.
/// </summary>
public unsafe partial class SubsetCrashRepro
{
    [LibraryImport("libHarfBuzzSharp")] private static partial byte* hb_blob_get_data(IntPtr blob, uint* length);

    private static byte[] LoadFont()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        return File.ReadAllBytes(Path.Combine(dir!.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"));
    }

    private static void CompactHeap()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    [Fact]
    public void CreateBlob_DataSurvivesHeapCompaction()
    {
        var font = LoadFont();
        using var blob = FontSubsetter.CreateBlob(font);
        for (int i = 0; i < 3; i++) { CompactHeap(); _ = new byte[1_000_000]; }

        uint len = 0;
        byte* data = hb_blob_get_data(blob.Handle, &len);
        Assert.Equal(font.Length, (int)len);
        Assert.True(new ReadOnlySpan<byte>(data, (int)len).SequenceEqual(font));
    }

    [Fact]
    public void Subset_RepeatedWithHeapCompaction_DoesNotCrash()
    {
        var font = LoadFont();
        var cps = "ABC123".Select(c => (int)c).ToArray();
        // 고치기 전에는 이 반복(400회)에서 약 60% 확률로 호스트가 죽었다 (GC가 Subset 도중에 일어나야 죽으므로 확률적)
        for (int i = 0; i < 400; i++)
        {
            if (i % 50 == 0) CompactHeap();
            Assert.NotEmpty(FontSubsetter.Subset(font, cps));
        }
    }
}
