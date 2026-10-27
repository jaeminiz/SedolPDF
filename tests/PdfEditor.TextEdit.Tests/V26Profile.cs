using System.Diagnostics;
using System.Text;
using Pdf.Native;
using PdfEditor.ContentStream;
using PdfSharp.Pdf.IO;

namespace PdfEditor.TextEdit.Tests;

/// <summary>
/// 0.4.0 V26: 큰 도면(A1, 205,000 객체) 쪽의 "출력 사본 + 문단 편집" 약 4 s가 어느 단계에서 드는지를 단계별로 다시 잰다
/// (M5 Task 10 기록: 객체 경계 0.2 + 시험 적용 ~1.1 + 화면 모양 0.6 + 실제 쓰기 1.2 + 저장 뒤 비교 0.7). 앱 코드를 바꾸지 않고 같은 부품을 직접 불러 단계 시간을 잰다.
/// 표본은 `spikes/out/bench-a1-paragraph.pdf`(없으면 건너뜀). 수치는 임시 폴더 v040-v26.txt.
/// </summary>
public sealed class V26Profile : IDisposable
{
    private readonly PdfiumThread _t = new();
    public void Dispose() => _t.Dispose();

    private static string? Sample()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "spikes", "out", "bench-a1-paragraph.pdf");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static double Ms(Stopwatch sw) { double v = sw.Elapsed.TotalMilliseconds; sw.Restart(); return v; }

    [Fact]
    public async Task V26_StageTimes_OnLargeDrawing()
    {
        string? path = Sample();
        if (path is null) return;
        var sb = new StringBuilder();
        sb.AppendLine("== V26 단계별 시간(ms, 웜업 1회 뒤 3회 중 중앙값): A1 205,000 객체 1쪽, 문단 편집 1개에 해당하는 작업을 부품별로 직접 호출");
        var rows = new Dictionary<string, List<double>>();
        void Add(string k, double v) { if (!rows.TryGetValue(k, out var l)) rows[k] = l = []; l.Add(v); }
        for (int round = 0; round < 4; round++)
        {
            var sw = Stopwatch.StartNew();
            byte[] bytes = File.ReadAllBytes(path);
            double read = Ms(sw);
            using var doc = PdfReader.Open(new MemoryStream(bytes), null, PdfDocumentOpenMode.Modify);
            double open = Ms(sw);
            var page = doc.Pages[0];
            var loaded = PageContent.Load(page);
            double load = Ms(sw);
            var parsed = ContentLexer.Parse(loaded.Data!);
            double lex = Ms(sw);
            var resolver = PageContent.XObjectResolver(page);
            var seg = ContentSegmenter.Segment(parsed, resolver);
            double segm = Ms(sw);

            // PDFium: 객체 종류·경계 목록(ReferenceObj 읽기)
            var refs = await _t.InvokeAsync(() =>
            {
                using var d = Pdf.Native.PdfDocument.Open(_t, path, null);
                using var p = d.LoadPage(0);
                var list = new List<ReferenceObject>(p.ObjectCount);
                for (int i = 0; i < p.ObjectCount; i++)
                {
                    var o = p.GetObject(i);
                    var b = o.Bounds;
                    list.Add(new ReferenceObject((ObjectKind)(int)o.Type, new Box(b.Left, b.Bottom, b.Right, b.Top)));
                }
                return list;
            });
            double pdfiumRefs = Ms(sw);
            var map = ObjectMap.Verify(seg, refs);
            double verify = Ms(sw);

            // 고쳐 쓰기(객체 하나 지움 + 새 글 덧붙임): 안에서 다시 파싱·분할해 검증한다
            var firstText = seg.Objects.Select((o, i) => (o, i)).First(x => x.o.Kind == ObjectKind.Text).i;
            var res = ContentRewriter.Rewrite(parsed, seg, [new DisableObject(firstText, RemoveText: true)], [Encoding.ASCII.GetBytes("BT /F1 10 Tf 100 100 Td (x) Tj ET")], resolver);
            double rewrite = Ms(sw);

            // PdfSharp: 쪽 내용 교체 + 저장
            page.Contents.Elements.Clear();
            page.Contents.AppendContent().CreateStream(res.Data ?? loaded.Data!);
            double replace = Ms(sw);
            using var ms = new MemoryStream();
            doc.Save(ms, false);
            double save = Ms(sw);

            // 저장 뒤 비교: 다시 열고 640 px 렌더
            string tmp = Path.Combine(Path.GetTempPath(), "v026-saved.pdf");
            File.WriteAllBytes(tmp, ms.ToArray());
            double write = Ms(sw);
            var (tOpen, tLoad, tFull, tRegion) = await _t.InvokeAsync(() =>
            {
                var s2 = Stopwatch.StartNew();
                using var d = Pdf.Native.PdfDocument.Open(_t, tmp, null);
                double a = s2.Elapsed.TotalMilliseconds; s2.Restart();
                using var p = d.LoadPage(0);
                double b = s2.Elapsed.TotalMilliseconds; s2.Restart();
                var sz = p.SizePt;
                double scale = 640.0 / sz.Width;
                p.RenderTile(scale, 0, 0, 640, (int)Math.Ceiling(sz.Height * scale), withAnnotations: false);
                double c = s2.Elapsed.TotalMilliseconds; s2.Restart();
                p.RenderTile(scale, 0, 0, 160, 100, withAnnotations: false);   // 편집 상자 주변만(영역 비교 가정)
                double e = s2.Elapsed.TotalMilliseconds;
                return (a, b, c, e);
            });
            double reopenRender = Ms(sw);
            Add("  (그 중) PDFium 문서 열기", tOpen); Add("  (그 중) PDFium 쪽 불러오기(내용 해석)", tLoad);
            Add("  (그 중) 640 px 전체 렌더", tFull); Add("  (그 중) 작은 영역(160x100 px)만 렌더", tRegion);

            if (round == 0) continue;   // 웜업
            Add("파일 읽기(4.4 MB)", read); Add("PdfSharp 열기(Modify)", open); Add("쪽 내용 풀기(PageContent.Load)", load);
            Add("ContentLexer.Parse", lex); Add("ContentSegmenter.Segment", segm); Add("PDFium 객체 종류·경계 읽기(20만 개)", pdfiumRefs);
            Add("ObjectMap.Verify", verify); Add("ContentRewriter.Rewrite(내부 재해석 포함)", rewrite);
            Add("PdfSharp 쪽 내용 교체", replace); Add("PdfSharp 저장(메모리)", save); Add("파일 쓰기", write); Add("저장본 다시 열기+640px 렌더(PDFium)", reopenRender);
        }
        double total = 0;
        foreach (var (k, l) in rows)
        {
            var s = l.OrderBy(x => x).ToList();
            double med = s[s.Count / 2];
            if (!k.StartsWith("  ")) total += med;
            sb.AppendLine($"{k,-48}: {med,8:0.0}");
        }
        sb.AppendLine($"{"합계(한 번 통과, 들여쓴 세부 줄 제외)",-48}: {total,8:0.0}");
        sb.AppendLine($"쪽 내용 크기: {File.ReadAllBytes(path).Length / 1_000_000.0:0.0} MB 파일, 해석 연산 수와 객체 수는 V22 성능 절 참조");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "v040-v26.txt"), sb.ToString());
    }
}
