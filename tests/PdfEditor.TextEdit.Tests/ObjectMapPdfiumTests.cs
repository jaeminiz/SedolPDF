using PdfEditor.ContentStream;
using Pdf.Native;
using SharpDocument = PdfSharp.Pdf.PdfDocument;

namespace PdfEditor.TextEdit.Tests;

/// <summary>
/// M4x Task 4: 새 해석기(PdfEditor.ContentStream)의 객체 목록을 PDFium 객체 목록과 종류 + 경계 사각형까지 대조한다(ObjectMap.Verify).
/// 합성 PDF는 전부 일치해야 하고, 실제 PDF(private/, 있을 때만)는 일치율을 임시 폴더 m4x-objectmap-report.txt에 남긴다(파일 이름은 적지 않음).
/// </summary>
public sealed class ObjectMapPdfiumTests : IDisposable
{
    private readonly PdfiumThread _t = new();
    public void Dispose() => _t.Dispose();

    private List<ReferenceObject> PdfiumObjects(string path, int pageIndex) =>
        _t.InvokeAsync(() =>
        {
            using var d = Pdf.Native.PdfDocument.Open(_t, path, null);
            using var p = d.LoadPage(pageIndex);
            var list = new List<ReferenceObject>();
            for (int i = 0; i < p.ObjectCount; i++)
            {
                var o = p.GetObject(i);
                var b = o.Bounds;
                list.Add(new ReferenceObject((ObjectKind)(int)o.Type, new Box(b.Left, b.Bottom, b.Right, b.Top)));
            }
            return list;
        }).GetAwaiter().GetResult();

    internal static ObjectMapResult Map(SharpDocument doc, string path, int p, ObjectMapPdfiumTests self)
    {
        var page = doc.Pages[p];
        var loaded = PageContent.Load(page);
        if (loaded.Data is null) return new ObjectMapResult { Ok = false, Reason = loaded.Failure };
        var seg = ContentSegmenter.Segment(ContentLexer.Parse(loaded.Data), PageContent.XObjectResolver(page));
        return ObjectMap.Verify(seg, self.PdfiumObjects(path, p));
    }

    [Fact]
    public void Verify_SyntheticAll_And_RealPdfsReport()
    {
        var files = new List<(string Label, string Path, bool Real)>();
        string tmp = Path.Combine(Path.GetTempPath(), "m4x-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string d1 = Path.Combine(tmp, "doc.pdf"), d2 = Path.Combine(tmp, "dwg.pdf");
            PdfEditor.TestFixtures.ContentPdfFactory.CreateDocumentLike(d1);
            PdfEditor.TestFixtures.ContentPdfFactory.CreateDrawingLike(d2);
            files.Add(("synthetic-document", d1, false)); files.Add(("synthetic-drawing", d2, false));
            foreach (var (name, bytes) in ColorSpaceFidelityTests.Cases())
            {
                string pth = Path.Combine(tmp, name + ".pdf");
                File.WriteAllBytes(pth, bytes);
                files.Add((name, pth, false));
            }
            string? priv = null;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "private"))) { priv = Path.Combine(d.FullName, "private"); break; }
            int n = 0;
            if (priv is not null) foreach (var f in Directory.GetFiles(priv, "*.pdf")) files.Add(($"real{++n}", f, true));

            int maxReal = int.TryParse(Environment.GetEnvironmentVariable("M4X_MAP_MAXPAGES"), out var mp) ? mp : 40;
            var report = new List<string>();
            int synthBad = 0;
            int allPages = 0, allOk = 0, kindBad = 0, boundsBad = 0, countBad = 0, checkedTotal = 0, skippedTotal = 0;
            foreach (var (label, path, real) in files)
            {
                var doc = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                int pages = real ? Math.Min(doc.PageCount, maxReal) : doc.PageCount;
                int ok = 0; var diffs = new List<string>();
                for (int p = 0; p < pages; p++)
                {
                    var r = Map(doc, path, p, this);
                    allPages++;
                    if (r.Ok) { ok++; allOk++; checkedTotal += r.BoundsChecked; skippedTotal += r.BoundsSkipped; }
                    else
                    {
                        if (r.Reason!.StartsWith("object count")) countBad++;
                        else if (r.Reason.StartsWith("kind")) kindBad++;
                        else boundsBad++;
                        if (!real) synthBad++;
                        if (diffs.Count < 5) diffs.Add($"p{p + 1} {r.Reason}");
                    }
                }
                report.Add($"{label}: {ok}/{pages} verified");
                report.AddRange(diffs.Select(x => "    " + x));
            }
            report.Add($"TOTAL {allOk}/{allPages} verified; rejected: count {countBad}, kind {kindBad}, bounds/other {boundsBad}; objects bounds-checked {checkedTotal}, kind-only {skippedTotal}");
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "m4x-objectmap-report.txt"), report);
            Assert.Equal(0, synthBad);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
}
