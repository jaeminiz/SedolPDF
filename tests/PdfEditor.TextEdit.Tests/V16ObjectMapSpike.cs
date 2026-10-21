using Pdf.Native;
using SharpPage = PdfSharp.Pdf.PdfPage;
using SharpDocument = PdfSharp.Pdf.PdfDocument;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

namespace PdfEditor.TextEdit.Tests;

/// <summary>
/// V16 (M4x Task 1): 쪽 내용 스트림을 해석해 "객체 목록"을 만들면 PDFium이 만드는 객체 목록(종류 순서)과 같은가?
/// 같아야만 PDFium 객체 번호(편집 기록의 OriginalIndex)로 스트림 안 연산자를 가리킬 수 있다.
/// 해석: PDFsharp ContentReader(연산자 열) + 아래 객체 분할 규칙. 결과는 임시 폴더 v16-report.txt에 쓴다(실제 PDF 이름은 적지 않음).
/// </summary>
public sealed class V16ObjectMapSpike : IDisposable
{
    private const int MaxPages = 40;   // 전체 쪽(710쪽) 측정은 결과 문서 — 시험은 빠르게 앞 40쪽만
    private readonly PdfiumThread _t = new();
    public void Dispose() => _t.Dispose();

    private static readonly HashSet<string> PathPaint = ["S", "s", "f", "F", "f*", "B", "B*", "b", "b*"];
    private static readonly HashSet<string> PathBuild = ["m", "l", "c", "v", "y", "re", "h"];

    /// <summary>해석기 객체 종류 열. 해석 불가면 null.</summary>
    internal static List<PdfPageObjectType>? Segment(SharpPage page, out string? failure)
    {
        failure = null;
        CSequence seq;
        try { seq = ContentReader.ReadContent(page); }
        catch (Exception e) { failure = "parse:" + e.GetType().Name; return null; }
        var result = new List<PdfPageObjectType>();
        int points = 0; bool closedDot = false;   // 점 2개 이상, 또는 `m h`(길이 0 닫힌 경로 — 둥근 끝 점 그리기)만 객체(실제 PDF 13건으로 확인)
        var xobjs = page.Resources?.Elements.GetDictionary("/XObject");
        foreach (var item in seq)
        {
            if (item is not COperator op) continue;
            string n = op.OpCode.Name;
            if (PathBuild.Contains(n)) { points += n switch { "re" => 4, "h" => 0, _ => 1 }; if (n == "h" && points == 1) closedDot = true; }
            else if (PathPaint.Contains(n)) { if (points > 1 || (points == 1 && (closedDot || n is "s" or "b" or "b*"))) result.Add(PdfPageObjectType.Path); points = 0; closedDot = false; }
            else if (n == "n") { points = 0; closedDot = false; }
            else if (n is "Tj" or "TJ" or "'" or "\"") { if (HasGlyphs(op)) result.Add(PdfPageObjectType.Text); }
            else if (n == "sh") result.Add(PdfPageObjectType.Shading);
            else if (n == "BI" || n == "ID") { if (n == "BI") result.Add(PdfPageObjectType.Image); }
            else if (n == "Do")
            {
                string? name = op.Operands.Count > 0 && op.Operands[0] is CName cn ? cn.Name : null;
                var x = name is null ? null : xobjs?.Elements.GetDictionary(name);
                string? sub = x?.Elements.GetName("/Subtype");
                if (sub == "/Image") result.Add(PdfPageObjectType.Image);
                else if (sub == "/Form") result.Add(PdfPageObjectType.Form);
            }
        }
        return result;
    }

    private static bool HasGlyphs(COperator op)
    {
        static bool Any(CObject o) => o switch { CString s => s.Value.Length > 0, CArray a => a.Any(Any), _ => false };
        return op.Operands.Any(Any);
    }

    private static string Key(IEnumerable<PdfPageObjectType> t) => string.Concat(t.Select(x => x switch
    {
        PdfPageObjectType.Text => 'T', PdfPageObjectType.Path => 'P', PdfPageObjectType.Image => 'I', PdfPageObjectType.Shading => 'S', PdfPageObjectType.Form => 'F', _ => '?',
    }));

    private (List<PdfPageObjectType> Types, List<PdfRectF> Bounds) PdfiumObjects(string path, int pageIndex) =>
        _t.InvokeAsync(() =>
        {
            using var d = Pdf.Native.PdfDocument.Open(_t, path, null);
            using var p = d.LoadPage(pageIndex);
            var types = new List<PdfPageObjectType>(); var bounds = new List<PdfRectF>();
            for (int i = 0; i < p.ObjectCount; i++) { var o = p.GetObject(i); types.Add(o.Type); bounds.Add(o.Bounds); }
            return (types, bounds);
        }).GetAwaiter().GetResult();

    private static string Where(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return $"first diff at {i} (ours {(i < a.Length ? a[i] : '-')} / pdfium {(i < b.Length ? b[i] : '-')}); ours {a.Length} pdfium {b.Length}";
    }

    [Fact]
    public void ObjectSequences_MatchPdfium_OnSyntheticAndPrivatePdfs()
    {
        var files = new List<(string Label, string Path)>();
        string tmp = Path.Combine(Path.GetTempPath(), "v16-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string d1 = Path.Combine(tmp, "doc.pdf"), d2 = Path.Combine(tmp, "dwg.pdf");
            PdfEditor.TestFixtures.ContentPdfFactory.CreateDocumentLike(d1);
            PdfEditor.TestFixtures.ContentPdfFactory.CreateDrawingLike(d2);
            files.Add(("synthetic-document", d1)); files.Add(("synthetic-drawing", d2));
            foreach (var (name, bytes) in ColorSpaceFidelityTests.Cases().Append(("edge-cases", ColorSpaceFidelityTests.Build("/ExtGState << /G << /ca 0.5 >> >>",
                "BT /F1 12 Tf 20 380 Td () Tj [(A) -50 (B)] TJ (C) ' 1 2 (D) \" 3 Tr (hidden) Tj 0 Tr ET\n" +        // 빈 글·TJ·'·"·보이지 않는 글
                "q 10 10 100 100 re W n 0 0 50 50 re f Q\n" +                                                      // 클립 + 채움
                "10 10 m f\n" +                                                                                     // 선분 없는 채움(객체 아님?)
                "0 0 m 100 100 l S 200 200 50 50 re B* 20 20 m 40 40 l 60 20 l h b\n" +
                "BI /W 2 /H 2 /CS /G /BPC 8 ID \x00\x80\xff\x40 EI\n" +                                            // 인라인 이미지
                "q /G gs 1 0 0 rg 300 300 40 40 re f Q BT /F1 9 Tf 50 50 Td (end) Tj ET\n", []))))
            {
                string pth = Path.Combine(tmp, name + ".pdf");
                File.WriteAllBytes(pth, bytes);
                files.Add((name, pth));
            }
            string? priv = null;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "private"))) { priv = Path.Combine(d.FullName, "private"); break; }
            int n = 0;
            if (priv is not null) foreach (var f in Directory.GetFiles(priv, "*.pdf")) files.Add(($"real{++n}", f));

            var report = new List<string>();
            int totalPages = 0, exact = 0, parseFail = 0;
            foreach (var (label, path) in files)
            {
                SharpDocument doc;
                try { doc = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import); }
                catch (Exception e) { report.Add($"{label}: open failed {e.GetType().Name}"); continue; }
                int pages = Math.Min(doc.PageCount, MaxPages), ex = 0, pf = 0;
                var diffs = new List<string>();
                for (int p = 0; p < pages; p++)
                {
                    totalPages++;
                    var ours = Segment(doc.Pages[p], out var fail);
                    if (ours is null) { pf++; parseFail++; diffs.Add($"p{p + 1} {fail}"); continue; }
                    var theirs = PdfiumObjects(path, p).Types;
                    string a = Key(ours), b = Key(theirs);
                    if (a == b) { ex++; exact++; }
                    else if (diffs.Count < 6) diffs.Add($"p{p + 1} {Where(a, b)} | ours={a} pdfium={b}");
                }
                report.Add($"{label}: {ex}/{pages} pages exact, {pf} parse failures");
                report.AddRange(diffs.Select(x => "    " + x));
            }
            report.Add($"TOTAL {exact}/{totalPages} exact, {parseFail} parse failures");
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "v16-report.txt"), report);
            Assert.Equal(totalPages, exact);   // 모든 쪽에서 해석기 객체 종류 열 == PDFium 객체 종류 열 (V16)
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
}
