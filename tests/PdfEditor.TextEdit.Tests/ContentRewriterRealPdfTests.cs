using PdfEditor.ContentStream;

namespace PdfEditor.TextEdit.Tests;

/// <summary>
/// M4x Task 2·5 실제 PDF 확인(private/ 가 있을 때만; 없으면 합성 PDF만): 모든 쪽에서 ① 연산자 범위 이어 붙이기 = 원본 바이트,
/// ② 객체를 몇 개씩 끄거나 옮긴 재작성이 균형·객체 수 검사를 통과한다. 수치는 임시 폴더 m4x-rewriter-report.txt.
/// </summary>
public sealed class ContentRewriterRealPdfTests
{
    [Fact]
    public void RoundTrip_And_RandomEdits_OnSyntheticAndRealPages()
    {
        var files = new List<string>();
        string tmp = Path.Combine(Path.GetTempPath(), "m4x-rw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string d1 = Path.Combine(tmp, "doc.pdf"), d2 = Path.Combine(tmp, "dwg.pdf");
            PdfEditor.TestFixtures.ContentPdfFactory.CreateDocumentLike(d1);
            PdfEditor.TestFixtures.ContentPdfFactory.CreateDrawingLike(d2);
            files.Add(d1); files.Add(d2);
            int synth = files.Count;
            foreach (var (name, bytes) in ColorSpaceFidelityTests.Cases())
            {
                string p = Path.Combine(tmp, name + ".pdf");
                File.WriteAllBytes(p, bytes);
                files.Add(p);
            }
            synth = files.Count;
            string? priv = null;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "private"))) { priv = Path.Combine(d.FullName, "private"); break; }
            if (priv is not null) files.AddRange(Directory.GetFiles(priv, "*.pdf"));

            int maxReal = int.TryParse(Environment.GetEnvironmentVariable("M4X_MAP_MAXPAGES"), out var mp) ? mp : 40;
            int pages = 0, roundTripBad = 0, rewriteOk = 0, rewriteRefused = 0, rewriteBad = 0;
            var refusedWhy = new Dictionary<string, int>();
            var rnd = new Random(11);
            var report = new List<string>();
            for (int fi = 0; fi < files.Count; fi++)
            {
                var doc = PdfSharp.Pdf.IO.PdfReader.Open(files[fi], PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                int n = fi < synth ? doc.PageCount : Math.Min(doc.PageCount, maxReal);
                for (int p = 0; p < n; p++)
                {
                    var page = doc.Pages[p];
                    var loaded = PageContent.Load(page);
                    if (loaded.Data is null) continue;
                    pages++;
                    var pc = ContentLexer.Parse(loaded.Data);
                    if (!pc.Ok) { roundTripBad++; continue; }
                    var ms = new MemoryStream(); int pos = 0; bool ordered = true;
                    foreach (var op in pc.Ops)
                    {
                        if (op.Start < pos || op.End <= op.OpStart) { ordered = false; break; }
                        ms.Write(pc.Data, pos, op.Start - pos); ms.Write(pc.Data, op.Start, op.End - op.Start); pos = op.End;
                    }
                    ms.Write(pc.Data, pos, pc.Data.Length - pos);
                    if (!ordered || !ms.ToArray().AsSpan().SequenceEqual(pc.Data)) roundTripBad++;

                    var xr = PageContent.XObjectResolver(page);
                    var seg = ContentSegmenter.Segment(pc, xr);
                    if (!seg.Ok || seg.Objects.Count == 0) continue;
                    var edits = new List<ObjectEdit>();
                    for (int k = 0; k < 6; k++)
                    {
                        int idx = rnd.Next(seg.Objects.Count);
                        if (edits.Any(e => e.ObjectIndex == idx)) continue;
                        edits.Add(k % 2 == 0 ? new DisableObject(idx) : new TransformObject(idx, Mat.Translate(rnd.Next(-20, 20), rnd.Next(-20, 20))));
                    }
                    var r = ContentRewriter.Rewrite(pc, seg, edits, null, xr);
                    if (r.Ok) rewriteOk++;
                    else
                    {
                        // 거절(특이 CTM·글 위치 불명·클립 경로 등)은 안전한 결과, 균형·객체 수·해석 실패는 결함
                        bool refusal = r.Failure!.Contains("singular") || r.Failure.Contains("not movable") || r.Failure.Contains("clip") || r.Failure.Contains("inside path");
                        if (refusal) { rewriteRefused++; string key = r.Failure.Split(':').Last().Trim(); refusedWhy[key] = refusedWhy.GetValueOrDefault(key) + 1; }
                        else { rewriteBad++; if (report.Count < 8) report.Add($"BAD file{fi} p{p + 1}: {r.Failure}"); }
                    }
                }
            }
            report.Insert(0, $"pages {pages}; lexer round-trip bad {roundTripBad}; random-edit rewrites ok {rewriteOk}, refused {rewriteRefused}, defective {rewriteBad}");
            report.AddRange(refusedWhy.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"  refused: {kv.Key} x{kv.Value}"));
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "m4x-rewriter-report.txt"), report);
            Assert.Equal(0, roundTripBad);
            Assert.Equal(0, rewriteBad);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
}
