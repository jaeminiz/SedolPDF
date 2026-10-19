using System.Runtime.InteropServices;
using Pdf.Native.Interop;
using PdfEditor.TestFixtures;
using Xunit.Abstractions;

namespace Pdf.Native.Tests;

/// <summary>M4 검증용 최소 P/Invoke (Task 2에서 정식 래퍼로 옮긴다). PDFium 전용 스레드 안에서만 부른다.</summary>
internal static unsafe partial class ObjNative
{
    private const string Lib = "pdfium";
    [LibraryImport(Lib)] public static partial int FPDFPage_CountObjects(IntPtr page);
    [LibraryImport(Lib)] public static partial IntPtr FPDFPage_GetObject(IntPtr page, int index);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetType(IntPtr obj);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetBounds(IntPtr obj, float* l, float* b, float* r, float* t);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetMatrix(IntPtr obj, FsMatrix* m);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetMatrix(IntPtr obj, FsMatrix* m);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetIsActive(IntPtr obj, int active);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetIsActive(IntPtr obj, int* active);
    [LibraryImport(Lib)] public static partial IntPtr FPDFTextObj_GetFont(IntPtr textObj);
    [LibraryImport(Lib)] public static partial int FPDFTextObj_GetFontSize(IntPtr textObj, float* size);
    [LibraryImport(Lib)] public static partial IntPtr FPDFPageObj_CreateTextObj(IntPtr doc, IntPtr font, float size);
    [LibraryImport(Lib)] public static partial int FPDFText_SetText(IntPtr textObj, ushort* text);
    [LibraryImport(Lib)] public static partial nuint FPDFTextObj_GetText(IntPtr textObj, IntPtr textPage, ushort* buf, nuint len);
    [LibraryImport(Lib)] public static partial int FPDFPage_InsertObjectAtIndex(IntPtr page, IntPtr obj, nuint index);
    [LibraryImport(Lib)] public static partial int FPDFPage_GenerateContent(IntPtr page);
    [LibraryImport(Lib)] public static partial int FPDFFormObj_CountObjects(IntPtr form);
    [LibraryImport(Lib)] public static partial IntPtr FPDFFormObj_GetObject(IntPtr form, nuint index);
    [LibraryImport(Lib)] public static partial int FPDFFormObj_RemoveObject(IntPtr form, IntPtr obj);
    [LibraryImport(Lib)] public static partial void FPDFPageObj_Destroy(IntPtr obj);
    [LibraryImport(Lib)] public static partial nuint FPDFFont_GetBaseFontName(IntPtr font, byte* buf, nuint len);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetFillColor(IntPtr obj, uint r, uint g, uint b, uint a);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetFillColor(IntPtr obj, uint* r, uint* g, uint* b, uint* a);

    public const int Text = 1, Path = 2, Image = 3, Shading = 4, Form = 5;

    public static string? GetText(IntPtr obj, IntPtr textPage)
    {
        nuint n = FPDFTextObj_GetText(obj, textPage, null, 0);
        if (n <= 2) return n == 0 ? null : "";
        var buf = new ushort[(int)n / 2];
        fixed (ushort* p = buf) FPDFTextObj_GetText(obj, textPage, p, n);
        return new string(buf.Select(c => (char)c).ToArray()).TrimEnd('\0');
    }

    public static bool SetText(IntPtr obj, string s)
    {
        var buf = (s + "\0").Select(c => (ushort)c).ToArray();
        fixed (ushort* p = buf) return FPDFText_SetText(obj, p) != 0;
    }

    public static FsMatrix GetMatrix(IntPtr obj) { FsMatrix m; FPDFPageObj_GetMatrix(obj, &m); return m; }
    public static bool SetMatrix(IntPtr obj, FsMatrix m) => FPDFPageObj_SetMatrix(obj, &m) != 0;
    public static float FontSize(IntPtr obj) { float s; FPDFTextObj_GetFontSize(obj, &s); return s; }

    public static (float L, float B, float R, float T) Bounds(IntPtr obj)
    {
        float l, b, r, t;
        FPDFPageObj_GetBounds(obj, &l, &b, &r, &t);
        return (l, b, r, t);
    }

    public static string FontName(IntPtr font)
    {
        nuint n = FPDFFont_GetBaseFontName(font, null, 0);
        if (n == 0) return "";
        var buf = new byte[(int)n];
        fixed (byte* p = buf) FPDFFont_GetBaseFontName(font, p, n);
        return System.Text.Encoding.UTF8.GetString(buf).TrimEnd('\0');
    }
}

/// <summary>
/// M4 계획서 Task 1: V12(replay 성립), V13(GenerateContent 충실도), V14(원래 글꼴 글자 확인), V15(폼 안 객체).
/// 결과는 docs/validation/2026-10-m4-results.md. 출력(ITestOutputHelper)에 수치를 남긴다.
/// </summary>
public sealed class V12ToV15ProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-m4probe-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly ITestOutputHelper _out;

    public V12ToV15ProbeTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    // ---- 도우미 ----

    private static List<(int Index, int Type)> List(PdfPage page)
    {
        int n = ObjNative.FPDFPage_CountObjects(page.Handle);
        var r = new List<(int, int)>(n);
        for (int i = 0; i < n; i++) r.Add((i, ObjNative.FPDFPage_GetObject(page.Handle, i) is var o ? ObjNative.FPDFPageObj_GetType(o) : 0));
        return r;
    }

    private static int FindText(PdfPage page, PdfTextPage tp, string contains)
    {
        int n = ObjNative.FPDFPage_CountObjects(page.Handle);
        for (int i = 0; i < n; i++)
        {
            var o = ObjNative.FPDFPage_GetObject(page.Handle, i);
            if (ObjNative.FPDFPageObj_GetType(o) != ObjNative.Text) continue;
            if ((ObjNative.GetText(o, tp.Handle) ?? "").Contains(contains, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    private static byte[] Render(PdfPage page, double scale)
    {
        var s = page.SizePt;
        int w = (int)Math.Ceiling(s.Width * scale), h = (int)Math.Ceiling(s.Height * scale);
        return page.RenderTile(scale, 0, 0, w, h, withAnnotations: false);
    }

    private static (double DiffRatio, int MaxChannel) Compare(byte[] a, byte[] b, int tolerance)
    {
        Assert.Equal(a.Length, b.Length);
        int diff = 0, max = 0, px = a.Length / 4;
        for (int i = 0; i < a.Length; i += 4)
        {
            int d = Math.Max(Math.Abs(a[i] - b[i]), Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2])));
            if (d > max) max = d;
            if (d > tolerance) diff++;
        }
        return ((double)diff / px, max);
    }

    // ---- V12 ----

    [Fact]
    public async Task V12_ReplayOnFreshPage_NoGenerateContent_ReloadPristine_CopyKeepsEdits()
    {
        ContentPdfFactory.CreateDocumentLike(P("doc.pdf"));
        string saved = P("copy.pdf");
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, P("doc.pdf"));
            List<(int, int)> before;
            byte[] pristine;
            using (var page = doc.LoadPage(0)) { before = List(page); pristine = Render(page, 0.5); }
            _out.WriteLine("objects: " + string.Join(",", before.Select(x => x.Item2)));
            Assert.Contains(before, x => x.Item2 == ObjNative.Image);

            void Edit(PdfPage page, PdfTextPage tp)
            {
                int img = before.First(x => x.Item2 == ObjNative.Image).Item1;
                int arial = FindText(page, tp, "Arial");
                Assert.True(arial >= 0);
                var oImg = ObjNative.FPDFPage_GetObject(page.Handle, img);
                var oArial = ObjNative.FPDFPage_GetObject(page.Handle, arial);
                // ① 지우기(끄기) ② 옮기기 ③ 원래 글꼴로 새 줄
                Assert.NotEqual(0, ObjNative.FPDFPageObj_SetIsActive(oImg, 0));
                var rect = before.First(x => x.Item2 == ObjNative.Path).Item1;
                var oPath = ObjNative.FPDFPage_GetObject(page.Handle, rect);
                var m = ObjNative.GetMatrix(oPath);
                m.E += 100;
                Assert.True(ObjNative.SetMatrix(oPath, m));
                float size = ObjNative.FontSize(oArial);
                var font = ObjNative.FPDFTextObj_GetFont(oArial);
                var nt = ObjNative.FPDFPageObj_CreateTextObj(doc.Handle, font, size);
                Assert.NotEqual(IntPtr.Zero, nt);
                Assert.True(ObjNative.SetText(nt, "Arial 321 CBA"));
                var am = ObjNative.GetMatrix(oArial);
                am.F -= 30;
                ObjNative.SetMatrix(nt, am);
                Assert.NotEqual(0, ObjNative.FPDFPage_InsertObjectAtIndex(page.Handle, nt, (nuint)(arial + 1)));
            }

            // 화면용 replay: 편집 → 렌더가 바뀜, 검색(새 텍스트 페이지)에 새 글이 보임
            using (var page = doc.LoadPage(0))
            {
                using (var tp0 = page.LoadText()) Edit(page, tp0);
                var edited = Render(page, 0.5);
                Assert.True(Compare(pristine, edited, 8).DiffRatio > 0.001, "edits must change the rendering");
                using var tp = page.LoadText();
                string all = tp.GetText(0, tp.CharCount);
                _out.WriteLine("text after edit: " + all.Replace("\r\n", " | "));
                Assert.Contains("Arial 321 CBA", all);
            }
            // GenerateContent 없이 닫았다 다시 열기: 원래대로
            for (int k = 0; k < 10; k++)
                using (var page = doc.LoadPage(0))
                {
                    Assert.Equal(before, List(page));
                    if (k == 0) Assert.Equal(0.0, Compare(pristine, Render(page, 0.5), 8).DiffRatio);
                }
            // 사본에서 같은 편집 + GenerateContent + 저장 → 다시 열면 편집이 남고 끈 이미지는 없다
            doc.SaveCopy(saved);
        });
        await _t.InvokeAsync(() =>
        {
            using var copy = PdfDocument.Open(_t, saved);
            using (var page = copy.LoadPage(0))
            {
                using (var tp = page.LoadText())
                {
                    // 같은 Edit를 사본에 (FindText는 사본 기준)
                    var before = List(page);
                    int img = before.First(x => x.Item2 == ObjNative.Image).Item1;
                    ObjNative.FPDFPageObj_SetIsActive(ObjNative.FPDFPage_GetObject(page.Handle, img), 0);
                    int arial = FindText(page, tp, "Arial");
                    var oArial = ObjNative.FPDFPage_GetObject(page.Handle, arial);
                    float size = ObjNative.FontSize(oArial);
                    var nt = ObjNative.FPDFPageObj_CreateTextObj(copy.Handle, ObjNative.FPDFTextObj_GetFont(oArial), size);
                    ObjNative.SetText(nt, "Arial 321 CBA");
                    var am = ObjNative.GetMatrix(oArial); am.F -= 30;
                    ObjNative.SetMatrix(nt, am);
                    ObjNative.FPDFPage_InsertObjectAtIndex(page.Handle, nt, (nuint)(arial + 1));
                }
                Assert.NotEqual(0, ObjNative.FPDFPage_GenerateContent(page.Handle));
            }
            copy.SaveCopy(P("copy2.pdf"));
        });
        await _t.InvokeAsync(() =>
        {
            using var d2 = PdfDocument.Open(_t, P("copy2.pdf"));
            using var page = d2.LoadPage(0);
            var after = List(page);
            _out.WriteLine("objects after generate: " + string.Join(",", after.Select(x => x.Item2)));
            Assert.DoesNotContain(after, x => x.Item2 == ObjNative.Image);   // 끈 이미지는 파일에 없다
            using var tp = page.LoadText();
            Assert.Contains("Arial 321 CBA", tp.GetText(0, tp.CharCount));
        });
    }

    // ---- V13 ----

    [Theory]
    [InlineData("document")]
    [InlineData("drawing")]
    public async Task V13_GenerateContentWithoutEdits_RenderingUnchanged(string kind)
    {
        string src = P(kind + ".pdf");
        if (kind == "document") ContentPdfFactory.CreateDocumentLike(src); else ContentPdfFactory.CreateDrawingLike(src);
        var (ratio, max, sizeBefore, sizeAfter) = await RoundTripGenerate(src);
        _out.WriteLine($"{kind}: diff ratio {ratio:P3}, max channel {max}, size {sizeBefore} -> {sizeAfter} bytes");
        Assert.True(ratio < 0.001, $"GenerateContent changed {ratio:P3} of pixels");
    }

    /// <summary>private/ 폴더의 실제 PDF(커밋하지 않음)가 있으면 앞 3쪽을 같은 방법으로 잰다. 없으면 건너뛴다. 이름은 출력하지 않는다.</summary>
    [Fact]
    public async Task V13_PrivateSamples_IfPresent()
    {
        string dir = FindPrivateDir();
        if (dir is null) { _out.WriteLine("private/ not found — skipped"); return; }
        int n = 0;
        foreach (var f in Directory.GetFiles(dir, "*.pdf").OrderBy(x => x))
        {
            n++;
            for (int p = 0; p < 3; p++)
            {
                try
                {
                    var r = await RoundTripGenerate(f, p);
                    _out.WriteLine($"sample {n} page {p + 1}: diff {r.Ratio:P3}, max {r.Max}, size {r.Before} -> {r.After}");
                }
                catch (Exception e) when (e is ArgumentOutOfRangeException or PdfiumException) { break; }
                catch (Exception e) { _out.WriteLine($"sample {n} page {p + 1}: {e.GetType().Name}"); break; }
            }
        }
    }

    private static string FindPrivateDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "private"))) d = d.Parent;
        return d is null ? null! : Path.Combine(d.FullName, "private");
    }

    private async Task<(double Ratio, int Max, long Before, long After)> RoundTripGenerate(string src, int pageIndex = 0)
    {
        string outPath = P("gen-" + Guid.NewGuid().ToString("N") + ".pdf");
        byte[] before = [], after = [];
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, src);
            if (pageIndex >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            double scale;
            using (var page = doc.LoadPage(pageIndex))
            {
                scale = 1200.0 / Math.Max(page.SizePt.Width, page.SizePt.Height);
                before = Render(page, scale);
                Assert.NotEqual(0, ObjNative.FPDFPage_GenerateContent(page.Handle));
            }
            doc.SaveCopy(outPath);
            using var d2 = PdfDocument.Open(_t, outPath);
            using var p2 = d2.LoadPage(pageIndex);
            after = Render(p2, scale);
        });
        var (ratio, max) = Compare(before, after, 8);
        return (ratio, max, new FileInfo(src).Length, new FileInfo(outPath).Length);
    }

    // ---- V14 ----

    /// <summary>
    /// 원래 글꼴로 새 글을 넣은 뒤, 그 객체만 남기고 렌더해서 글자 칸마다 잉크가 있는지 본다.
    /// 되읽기(GetText)와 함께 잰다. 서브셋에 없는 글자를 넣으면 반드시 걸려야 한다(거짓 통과 0).
    /// </summary>
    [Theory]
    [InlineData("Arial", "Arial 321 CBA", true)]           // 서브셋에 있는 글자만 (A r i l 1 2 3 B C 공백)
    [InlineData("Arial", "Zebra Quiz 789", false)]         // 서브셋에 없는 글자 섞임 (Z e b Q u z 7 8 9)
    [InlineData("가나다", "다나가 검토", true)]               // 원래 글에 있던 한글만
    [InlineData("가나다", "라마바 검토", false)]              // 서브셋에 없는 한글
    [InlineData("Helvetica", "Zebra Quiz 789", true)]      // 비임베드 표준 글꼴: 전체 글자 있음
    public async Task V14_OriginalFontCoverageCheck(string lineContains, string newText, bool expectedOk)
    {
        ContentPdfFactory.CreateDocumentLike(P("doc.pdf"));
        var r = await CheckCoverage(P("doc.pdf"), lineContains, newText);
        _out.WriteLine($"{lineContains} -> '{newText}': font={r.Font}, readBack='{r.ReadBack}', inkedChars={r.Inked}/{r.NonSpace}, ok={r.Ok}");
        Assert.Equal(expectedOk, r.Ok);
    }

    [Fact]
    public async Task V14_NoToUnicode_KoreanLine()
    {
        ContentPdfFactory.CreateDocumentLike(P("noto.pdf"), stripToUnicode: true);
        var same = await CheckCoverage(P("noto.pdf"), null, "다나가 검토", koreanLine: true);
        var missing = await CheckCoverage(P("noto.pdf"), null, "라마바 검토", koreanLine: true);
        _out.WriteLine($"no ToUnicode, same glyphs: readBack='{same.ReadBack}', inked={same.Inked}/{same.NonSpace}, ok={same.Ok}");
        _out.WriteLine($"no ToUnicode, missing glyphs: readBack='{missing.ReadBack}', inked={missing.Inked}/{missing.NonSpace}, ok={missing.Ok}");
        Assert.False(missing.Ok);   // 거짓 통과는 허용하지 않는다. same.Ok는 기록만(ToUnicode가 없으면 PDFium이 글자를 고를 수 없을 수 있음)
    }

    private sealed record Coverage(string Font, string? ReadBack, int Inked, int NonSpace, bool Ok);

    private async Task<Coverage> CheckCoverage(string path, string? lineContains, string newText, bool koreanLine = false)
    {
        return await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, path);
            using var page = doc.LoadPage(0);
            int idx;
            using (var tp0 = page.LoadText())
            {
                idx = lineContains is not null ? FindText(page, tp0, lineContains) : -1;
                if (koreanLine)
                {
                    // ToUnicode가 없으면 글로 찾을 수 없으므로 두 번째 텍스트 객체(맑은 고딕 줄)를 쓴다
                    int seen = 0, n = ObjNative.FPDFPage_CountObjects(page.Handle);
                    for (int i = 0; i < n && idx < 0; i++)
                        if (ObjNative.FPDFPageObj_GetType(ObjNative.FPDFPage_GetObject(page.Handle, i)) == ObjNative.Text && ++seen == 2) idx = i;
                }
            }
            Assert.True(idx >= 0);
            var orig = ObjNative.FPDFPage_GetObject(page.Handle, idx);
            var font = ObjNative.FPDFTextObj_GetFont(orig);
            float size = ObjNative.FontSize(orig);
            var nt = ObjNative.FPDFPageObj_CreateTextObj(doc.Handle, font, size);
            bool set = ObjNative.SetText(nt, newText);
            var m = ObjNative.GetMatrix(orig);
            ObjNative.SetMatrix(nt, m);
            ObjNative.FPDFPageObj_SetFillColor(nt, 0, 0, 0, 255);
            // 새 객체만 남긴다
            int count = ObjNative.FPDFPage_CountObjects(page.Handle);
            for (int i = 0; i < count; i++) ObjNative.FPDFPageObj_SetIsActive(ObjNative.FPDFPage_GetObject(page.Handle, i), 0);
            ObjNative.FPDFPage_InsertObjectAtIndex(page.Handle, nt, (nuint)count);

            using var tp = page.LoadText();
            string? readBack = ObjNative.GetText(nt, tp.Handle);
            // 글자 칸마다 잉크 검사 (텍스트 페이지의 글자 상자, 쪽 좌표 → 렌더 픽셀)
            double scale = 4;
            var sz = page.SizePt;
            int w = (int)Math.Ceiling(sz.Width * scale), h = (int)Math.Ceiling(sz.Height * scale);
            var (l, b, r, t) = ObjNative.Bounds(nt);
            int x0 = Math.Max(0, (int)(l * scale) - 2), x1 = Math.Min(w, (int)Math.Ceiling(r * scale) + 2);
            int y0 = Math.Max(0, (int)((sz.Height - t) * scale) - 2), y1 = Math.Min(h, (int)Math.Ceiling((sz.Height - b) * scale) + 2);
            var bmp = page.RenderTile(scale, x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0), withAnnotations: false);
            int bw = Math.Max(1, x1 - x0);
            int chars = tp.CharCount, inked = 0, nonSpace = 0;
            for (int c = 0; c < chars; c++)
            {
                string ch = tp.GetText(c, 1);
                if (string.IsNullOrWhiteSpace(ch)) continue;
                nonSpace++;
                foreach (var rect in tp.GetRects(c, 1))
                {
                    int cx0 = (int)(rect.Left * scale) - x0, cx1 = (int)Math.Ceiling(rect.Right * scale) - x0;
                    // RectangleF.FromLTRB(l, b, r, t): Top = 아래 y, Bottom = 위 y (PDF 좌표)
                    float lo = Math.Min(rect.Top, rect.Bottom), hi = Math.Max(rect.Top, rect.Bottom);
                    int cy0 = (int)((sz.Height - hi) * scale) - y0, cy1 = (int)Math.Ceiling((sz.Height - lo) * scale) - y0;
                    bool ink = false;
                    for (int y = Math.Max(0, cy0); y < Math.Min(y1 - y0, cy1) && !ink; y++)
                        for (int x = Math.Max(0, cx0); x < Math.Min(bw, cx1); x++)
                        {
                            int o = (y * bw + x) * 4;
                            if (bmp[o] < 128 && bmp[o + 1] < 128 && bmp[o + 2] < 128) { ink = true; break; }
                        }
                    if (ink) { inked++; break; }
                }
            }
            string expectedNoSpace = newText.Replace(" ", "");
            bool ok = set && readBack == newText && nonSpace == expectedNoSpace.Length && inked == nonSpace;
            return new Coverage(ObjNative.FontName(font), readBack, inked, nonSpace, ok);
        });
    }

    // ---- V15 ----

    [Fact]
    public async Task V15_FormChildEdits_SurviveGenerateContentOnCopy()
    {
        ContentPdfFactory.CreateDrawingLike(P("dwg.pdf"));
        string outPath = P("dwg-gen.pdf");
        int childCountBefore = 0;
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, P("dwg.pdf"));
            using var page = doc.LoadPage(0);
            var form = Enumerable.Range(0, ObjNative.FPDFPage_CountObjects(page.Handle))
                .Select(i => ObjNative.FPDFPage_GetObject(page.Handle, i))
                .First(o => ObjNative.FPDFPageObj_GetType(o) == ObjNative.Form);
            childCountBefore = ObjNative.FPDFFormObj_CountObjects(form);
            _out.WriteLine("outer form children: " + string.Join(",", Enumerable.Range(0, childCountBefore)
                .Select(i => ObjNative.FPDFPageObj_GetType(ObjNative.FPDFFormObj_GetObject(form, (nuint)i)))));
            // 첫 자식(선) 끄기, 텍스트 자식 옮기기, 이미지(안쪽 폼) 제거
            var line = ObjNative.FPDFFormObj_GetObject(form, 0);
            _out.WriteLine("SetIsActive(child) = " + ObjNative.FPDFPageObj_SetIsActive(line, 0));
            for (int i = 0; i < childCountBefore; i++)
            {
                var c = ObjNative.FPDFFormObj_GetObject(form, (nuint)i);
                if (ObjNative.FPDFPageObj_GetType(c) == ObjNative.Text)
                {
                    var m = ObjNative.GetMatrix(c); m.E += 200;
                    _out.WriteLine("SetMatrix(child text) = " + (ObjNative.SetMatrix(c, m) ? 1 : 0));
                    break;
                }
            }
            Assert.NotEqual(0, ObjNative.FPDFPage_GenerateContent(page.Handle));
            doc.SaveCopy(outPath);
        });
        await _t.InvokeAsync(() =>
        {
            using var d2 = PdfDocument.Open(_t, outPath);
            using var page = d2.LoadPage(0);
            var form = Enumerable.Range(0, ObjNative.FPDFPage_CountObjects(page.Handle))
                .Select(i => ObjNative.FPDFPage_GetObject(page.Handle, i))
                .First(o => ObjNative.FPDFPageObj_GetType(o) == ObjNative.Form);
            int after = ObjNative.FPDFFormObj_CountObjects(form);
            float tx = float.NaN;
            for (int i = 0; i < after; i++)
            {
                var c = ObjNative.FPDFFormObj_GetObject(form, (nuint)i);
                if (ObjNative.FPDFPageObj_GetType(c) == ObjNative.Text) { tx = ObjNative.Bounds(c).L; break; }
            }
            _out.WriteLine($"form children after: {after} (before {childCountBefore}); first text left = {tx}");
        });
    }
}
