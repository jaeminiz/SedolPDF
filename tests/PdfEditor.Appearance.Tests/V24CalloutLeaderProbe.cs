using System.Collections.Immutable;
using System.Text;
using Pdf.Native;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.TestFixtures;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Appearance.Tests;

/// <summary>
/// 0.4.0 V24: 설명선 지시선 2개의 PDF 표현. 기본안(D3) = 표준 /CL(첫 지시선) + 비표준 /SedolCL2(둘째 지시선) + AP가 둘 다 그림.
/// 확인: ① PDFium(= Chrome·Edge 같은 엔진)이 AP의 둘째 지시선을 그리는가 ② 패치 단계가 /SedolCL2를 보존하는가(다른 키만 고친 패치)
/// ③ 앱의 원본 주석 읽기(AnnotationReader)가 /SedolCL2가 든 설명선을 정상으로 읽는가. 다른 뷰어(Acrobat·PDF-XChange·Foxit)는 사람이 확인(검증 필요).
/// 수치는 임시 폴더 v040-v24.txt.
/// </summary>
public sealed class V24CalloutLeaderProbe : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v040-v24-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly PdfSharpAnnotationPatcher _patcher = new(() => []);

    public V24CalloutLeaderProbe() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Blank()
    {
        string p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        doc.Save(p);
        return p;
    }

    private static readonly PdfRectF Rect = new(60, 560, 420, 760);   // 상자 + 지시선을 모두 덮는 Rect

    private static string Ap(bool secondLeader)
    {
        var sb = new StringBuilder();
        sb.Append("q 1 1 0.6 rg 300 660 m 400 660 l 400 720 l 300 720 l h f Q\n");           // 상자(노랑)
        sb.Append("q 0.9 0.2 0.15 RG 1.5 w 300 660 100 60 re S Q\n");                          // 상자 테두리
        sb.Append("q 0.9 0.2 0.15 RG 1.5 w 100 600 m 200 640 l 300 690 l S Q\n");              // 첫 지시선 (꺾임 2구간)
        if (secondLeader) sb.Append("q 0.9 0.2 0.15 RG 1.5 w 380 580 m 380 620 l 350 660 l S Q\n");   // 둘째 지시선
        return sb.ToString();
    }

    private string Make(bool secondLeader, bool withCl2Key)
    {
        string p = Blank();
        var ops = ImmutableArray.CreateBuilder<PatchOp>();
        ops.Add(new SetName("/IT", "/FreeTextCallout".TrimStart('/')));
        ops.Add(new SetString("/Contents", "callout"));
        ops.Add(new SetString("/DA", "0 0 0 rg /Helv 12 Tf"));
        ops.Add(new SetNumbers("/CL", [100f, 600f, 200f, 640f, 300f, 690f]));
        if (withCl2Key) ops.Add(new SetNumbers("/SedolCL2", [380f, 580f, 380f, 620f, 350f, 660f]));
        ops.Add(new SetNumbers("/RD", [240f, 100f, 20f, 40f]));
        ops.Add(new SetAppearanceStream(Ap(secondLeader), Rect));
        _patcher.Apply(p, null, new PatchPlan([], [new AnnotationCreate(0, "FreeText", "v24", Rect, 4, ops.ToImmutable())], []), AnnotationAssets.Empty);
        return p;
    }

    private async Task<byte[]> Render(string path)
    {
        return await _t.InvokeAsync(() =>
        {
            using var d = Pdf.Native.PdfDocument.Open(_t, path, null);
            using var pg = d.LoadPage(0);
            var sz = pg.SizePt;
            return pg.RenderTile(1, 0, 0, (int)Math.Ceiling(sz.Width), (int)Math.Ceiling(sz.Height), withAnnotations: true);
        });
    }

    private static int Diff(byte[] a, byte[] b, Func<int, int, bool>? mask = null, int w = 595)
    {
        int n = 0;
        for (int i = 0; i + 3 < a.Length && i + 3 < b.Length; i += 4)
        {
            if (Math.Abs(a[i] - b[i]) <= 64 && Math.Abs(a[i + 1] - b[i + 1]) <= 64 && Math.Abs(a[i + 2] - b[i + 2]) <= 64) continue;
            if (mask is null || mask((i / 4) % w, (i / 4) / w)) n++;
        }
        return n;
    }

    [Fact]
    public async Task V24_TwoLeaders_PdfiumDrawsBoth_KeyKept_ReaderAccepts()
    {
        var sb = new StringBuilder();
        string two = Make(true, true), one = Make(false, false), twoNoKey = Make(true, false);
        var rTwo = await Render(two); var rOne = await Render(one); var rTwoNoKey = await Render(twoNoKey);
        // 둘째 지시선 영역(쪽 좌표 x 345~385, y 575~665 → 화면 y = 842 - y)
        bool InSecond(int x, int y) => x >= 345 && x <= 385 && 842 - y >= 575 && 842 - y <= 665;
        int secondVisible = Diff(rTwo, rOne, InSecond);
        int elsewhere = Diff(rTwo, rOne, (x, y) => !InSecond(x, y));
        int keyDoesNotAffectRender = Diff(rTwo, rTwoNoKey);
        sb.AppendLine($"PDFium render (withAnnotations): 둘째 지시선 영역 차이 {secondVisible}px (>0이면 AP의 둘째 지시선이 그려짐), 그 밖 차이 {elsewhere}px, /SedolCL2 키 유무에 따른 렌더 차이 {keyDoesNotAffectRender}px");
        Assert.True(secondVisible > 50);
        Assert.Equal(0, elsewhere);
        Assert.Equal(0, keyDoesNotAffectRender);

        // ② 패치 단계: 다른 키(/Contents)와 AP만 고치는 패치 → /SedolCL2 보존
        var patch = new AnnotationPatch(0, 0, "v24", [new SetString("/Contents", "edited"), new SetAppearanceStream(Ap(true), Rect)]);
        _patcher.Apply(two, null, new PatchPlan([patch], [], []), AnnotationAssets.Empty);
        using (var doc = PdfReader.Open(two, PdfDocumentOpenMode.Import))
        {
            var annots = (PdfArray)((doc.Pages[0].Elements["/Annots"] as PdfReference)?.Value ?? doc.Pages[0].Elements["/Annots"]!);
            var a = (PdfDictionary)((annots.Elements[0] as PdfReference)?.Value ?? annots.Elements[0]);
            var cl2 = a.Elements.GetArray("/SedolCL2");
            sb.AppendLine($"패치 뒤 /SedolCL2 보존: {(cl2 is { Elements.Count: 6 } ? "예 (6개 수)" : "아니오")}, /CL 점 {a.Elements.GetArray("/CL")?.Elements.Count / 2}개, /Contents={a.Elements.GetString("/Contents")}");
            Assert.NotNull(cl2);
            Assert.Equal(6, cl2!.Elements.Count);
        }

        // ③ 앱 원본 주석 읽기
        var back = AnnotationReader.Read(File.ReadAllBytes(two), null, [0]);
        var o = back[0].Single();
        sb.AppendLine($"AnnotationReader: Kind={o.Data?.Kind} Intent={o.Data?.Intent} Vertices={o.Data?.Vertices.Length} Reason={o.Reason}");
        Assert.Equal(AnnotKind.FreeText, o.Data?.Kind);
        Assert.Equal(FreeTextIntent.Callout, o.Data?.Intent);
        Assert.Equal(3, o.Data!.Vertices.Length);
        Assert.Equal(ReadOnlyReason.None, o.Reason);

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "v040-v24.txt"), sb.ToString());
        // 사람이 다른 뷰어로 열어 볼 수 있게 복사
        File.Copy(two, Path.Combine(Path.GetTempPath(), "v040-v24-two-leaders.pdf"), true);
        File.Copy(one, Path.Combine(Path.GetTempPath(), "v040-v24-one-leader.pdf"), true);
    }
}
