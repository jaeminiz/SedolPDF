using System.Text;
using PdfEditor.ContentStream;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Appearance.Tests;

/// <summary>
/// 0.14.0 안전망: 새 ContentRepair.RepairStreams(해석 1번)가 0.13.0 구현(ContentRepairReference)과 바이트 단위로 같은 결과를 내는지 본다.
/// 합성 시료 + private/ 의 PDF 전부 + SEDOL_BENCH_PDF(있을 때)의 모든 쪽.
/// </summary>
public class ContentRepairEquivalenceTests
{
    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    public static IEnumerable<object[]> Synthetic()
    {
        string[][] cases =
        [
            ["q", "q q", "q 0 0 0 RG 1 w 10 10 m 20 10 l S Q .75 0 0 -.75 0 841.92 cm", "S Q q 0 0 0 RG 1 J 30 30 m 40 30 l S Q q 0 0 0 RG", "1 w 50 50 m 60 50 l S Q q 0 0 0 RG 70 70 m 80 70 l S Q q", "0.32 w 90 90 m 95 95 l S Q Q", "Q q q BT /SedolF1 12 Tf (A) Tj ET Q Q Q", "Q q q BT /SedolF2 12 Tf (B) Tj ET Q Q"],
            ["q", "q q", "q 1 0 0 RG 2 w .75 0 0 -.75 0 841.92 cm 100 100 m 500 100 l S Q .75 0 0 -.75 0 841.92 cm", "S Q q 0 0 1 RG 2 w 100 300 m 500 300 l S Q", "Q", "Q Q"],
            ["q .75 0 0 -.75 0 841.92 cm q 1 0 0 RG 2 w 100 100 m 500 100 l S Q q 0 0 1 RG 2 w 100 300 m 500 300 l S Q Q"],
            ["q 1 0 0 RG 10 10 m 100 10 l S Q\n.75 0 0 -.75 0 841 cm\n1 J 0.5 w 20 50 m 200 50 l S Q\nq 0 1 0 rg 300 600 80 80 re f Q\n"],
            ["q 10 10 m 20 20 l S Q S Q q Q"],                                   // 합쳐진 꼬리 조각(RemoveDanglingFragments 길)
            ["q 1 0 0 RG 5 6", "S Q q 0 0 m 1 1 l S Q"],                         // 스트림 끝에 남은 피연산자
            ["q 1 0 0 RG", "5 6 m 7 8 l S Q Q"],
            ["q BI /W 1 /H 1 /BPC 8 /CS /G ID \u0001 EI Q", "S Q Q"],            // 인라인 이미지
            ["q 10 10 m 20 20 l S Q", "BT (a)) Tj ET Q"],                        // 해석 실패 → 쪽 전체 규칙
            ["q Q Q Q"],
            ["Q"],
            ["", ""],
            [],
            ["q", "q q", "S Q", "Q q Q", "10 10 m 20 20 l S Q Q"],
            ["q % comment Q\n 1 0 0 rg 0 0 10 10 re f Q Q", "% only comment"],
        ];
        foreach (var c in cases) yield return [c];
    }

    private static void AssertSame(IReadOnlyList<byte[]> streams, string what)
    {
        var o = ContentRepairReference.RepairStreams(streams);
        var n = ContentRepair.RepairStreams(streams);
        Assert.True(o.FragmentsRemoved == n.FragmentsRemoved && o.RestoresRemoved == n.RestoresRemoved, $"{what}: counts old=({o.FragmentsRemoved},{o.RestoresRemoved}) new=({n.FragmentsRemoved},{n.RestoresRemoved})");
        if (o.Data is null) Assert.True(n.Data is null, $"{what}: new returned data, old null");
        else { Assert.True(n.Data is not null, $"{what}: new null, old data"); Assert.True(o.Data.AsSpan().SequenceEqual(n.Data), $"{what}: bytes differ"); }
    }

    [Theory]
    [MemberData(nameof(Synthetic))]
    public void Synthetic_SameAsReference(string[] streams) => AssertSame(streams.Select(B).ToList(), string.Join("|", streams).Length > 60 ? string.Join("|", streams)[..60] : string.Join("|", streams));

    private static string? PrivateDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "private"))) return Path.Combine(d.FullName, "private");
        return null;
    }

    internal static IEnumerable<string> RealPdfs()
    {
        var env = Environment.GetEnvironmentVariable("SEDOL_BENCH_PDF");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) yield return env;
        var dir = PrivateDir();
        if (dir is not null)
            foreach (var f in Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories)) yield return f;
    }

    [Fact]
    public void RealPdfs_AllPages_SameAsReference()
    {
        int pages = 0;
        foreach (var path in RealPdfs())
        {
            PdfSharp.Pdf.PdfDocument doc;
            try { doc = PdfReader.Open(new MemoryStream(File.ReadAllBytes(path)), null, PdfDocumentOpenMode.Import); }
            catch { continue; }   // 암호·깨진 파일은 건너뜀
            using (doc)
                for (int i = 0; i < doc.PageCount; i++)
                {
                    var loaded = PageContent.LoadStreams(doc.Pages[i]);
                    if (loaded.Streams is null) continue;
                    AssertSame(loaded.Streams, $"{Path.GetFileName(path)} p{i + 1}");
                    pages++;
                }
        }
        Assert.True(pages >= 0);
    }
}
