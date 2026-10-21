using System.Text;
using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Content;
using PdfEditor.Document.Output;
using PdfEditor.Document.Saving;

namespace PdfEditor.TextEdit.Tests;

/// <summary>
/// V13 확대(M4 마무리 검토): 색 공간·그리기 방식별 합성 PDF를 편집(아무것도 안 움직이는 이동) → 저장 → 렌더해
/// 편집 전과 같은 모양인지 본다. 제작 프로그램별(Quartz = ICCBased, Word = CalRGB/패턴, CAD = Separation·셰이딩 등) 흉내.
/// </summary>
public sealed class ColorSpaceFidelityTests : IDisposable
{
    private readonly string _dir = Environment.GetEnvironmentVariable("V13X_KEEP") ?? Path.Combine(Path.GetTempPath(), "pdfeditor-v13x-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    public void Dispose() { _t.Dispose(); if (Environment.GetEnvironmentVariable("V13X_KEEP") is null) try { Directory.Delete(_dir, true); } catch { } }

    private const string SRgbIcc = @"C:\Windows\System32\spool\drivers\color\sRGB Color Space Profile.icm";
    private const string AdobeIcc = @"C:\Windows\System32\spool\drivers\color\AdobeRGB1998.icc";

    internal sealed record Obj(string Dict, byte[]? Stream = null);

    internal static byte[] Build(string resources, string content, IEnumerable<Obj> extra0)
    {
        var extra = extra0.ToList();
        // 1 catalog, 2 pages, 3 page, 4 content, 5 font, 6.. extra
        var ms = new MemoryStream();
        var offs = new List<long>();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        void O(int n, string dict, byte[]? stream = null)
        {
            offs.Add(ms.Position);
            W($"{n} 0 obj\n");
            if (stream is null) W(dict + "\n");
            else { W(dict.Replace(">>", $"/Length {stream.Length} >>") + "\nstream\n"); ms.Write(stream); W("\nendstream\n"); }
            W("endobj\n");
        }
        W("%PDF-1.5\n");
        O(1, "<< /Type /Catalog /Pages 2 0 R >>");
        O(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        O(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> {resources} >> >>");
        O(4, "<< >>", Encoding.Latin1.GetBytes(content));
        O(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        for (int i = 0; i < extra.Count; i++) O(6 + i, extra[i].Dict, extra[i].Stream);
        long xref = ms.Position;
        W($"xref\n0 {offs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offs) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {offs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static Obj[] Icc(byte[] icc) => [new("[/ICCBased 7 0 R]"), new("<< /N 3 /Alternate /DeviceRGB >>", icc)];
    private const string SepFn = "<< /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [0.9 0.1 0.1] /N 1 >>";

    internal static IEnumerable<(string Name, byte[] Pdf)> Cases()
    {
        byte[] srgb = File.Exists(SRgbIcc) ? File.ReadAllBytes(SRgbIcc) : File.ReadAllBytes(AdobeIcc);
        // 8x8 1비트 스텐실 마스크(체크무늬)
        byte[] mask = [0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55];
        string text = "BT /F1 24 Tf 20 360 Td (Hello 123) Tj ET\n";

        yield return ("calrgb", Build("/ColorSpace << /C 6 0 R >>",
            "/C cs 0.2 0.4 0.9 sc 20 200 150 100 re f /C CS 0.9 0.2 0.1 SC 4 w 200 200 150 100 re S /C cs 0.1 0.6 0.2 sc " + text,
            [new("[/CalRGB << /WhitePoint [0.9505 1 1.089] /Gamma [2.2 2.2 2.2] /Matrix [0.4124 0.2126 0.0193 0.3576 0.7152 0.1192 0.1805 0.0722 0.9505] >>]")]));

        yield return ("separation", Build("/ColorSpace << /S [/Separation /Spot /DeviceRGB " + SepFn + "] >>",
            "/S cs 0.8 sc 20 200 150 100 re f /S CS 1 SC 4 w 200 200 150 100 re S /S cs 0.5 sc " + text, []));

        yield return ("cmyk-gray", Build("",
            "0.2 0.7 0.1 0.1 k 20 200 150 100 re f 0.3 G 5 w 200 200 150 100 re S 0.6 g " + text + "1 0 0 0 K 3 w 20 100 m 380 100 l S", []));

        yield return ("icc-text-render-modes", Build("/ColorSpace << /Cs1 6 0 R >>",
            "/Cs1 cs 0.9 0.8 0.1 sc /Cs1 CS 0.1 0.1 0.8 SC 1 w BT /F1 48 Tf 20 250 Td 2 Tr (Fill+Stroke) Tj ET " +
            "BT /F1 48 Tf 20 150 Td 1 Tr (Stroke) Tj ET " + text, Icc(srgb)));

        yield return ("icc-transparency", Build("/ColorSpace << /Cs1 6 0 R >> /ExtGState << /G1 << /ca 0.5 /CA 0.5 >> /G2 << /BM /Multiply >> >>",
            "/Cs1 cs 0.2 0.5 0.9 sc 20 200 200 120 re f /G1 gs 0.9 0.2 0.1 sc 120 160 200 120 re f /G2 gs 0.1 0.8 0.2 sc 60 100 200 120 re f " + text, Icc(srgb)));

        yield return ("image-mask-icc-fill", Build("/ColorSpace << /Cs1 6 0 R >> /XObject << /M 8 0 R >>",
            "/Cs1 cs 0.8 0.1 0.1 sc q 150 0 0 150 20 150 cm /M Do Q /Cs1 cs 0.1 0.1 0.8 sc q 100 0 0 100 220 150 cm /M Do Q " + text,
            [.. Icc(srgb), new("<< /Type /XObject /Subtype /Image /Width 8 /Height 8 /ImageMask true /BitsPerComponent 1 >>", mask)]));

        yield return ("shading-sh", Build("/Shading << /Sh1 << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [20 0 380 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> /Extend [true true] >> >>",
            "q 20 150 360 120 re W n /Sh1 sh Q " + text, []));

        yield return ("shading-pattern-fill", Build("/Pattern << /P1 6 0 R >>",
            "/Pattern cs /P1 scn 20 150 360 120 re f /Pattern CS /P1 SCN 8 w 20 20 360 100 re S " + text,
            [new("<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [20 0 380 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0.8 0] /C1 [0 0.5 1] /N 1 >> /Extend [true true] >> >>")]));

        yield return ("tiling-pattern-colored", Build("/Pattern << /P1 6 0 R >>",
            "/Pattern cs /P1 scn 20 150 360 120 re f " + text,
            [new("<< /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 20 20] /XStep 20 /YStep 20 /Resources << >> >>",
                Encoding.Latin1.GetBytes("1 0 0 rg 0 0 10 10 re f 0 0 1 rg 10 10 10 10 re f"))]));

        yield return ("tiling-pattern-uncolored", Build("/ColorSpace << /PCS [/Pattern /DeviceRGB] >> /Pattern << /P1 6 0 R >>",
            "/PCS cs 0.1 0.7 0.2 /P1 scn 20 150 360 120 re f " + text,
            [new("<< /PatternType 1 /PaintType 2 /TilingType 1 /BBox [0 0 20 20] /XStep 20 /YStep 20 /Resources << >> >>",
                Encoding.Latin1.GetBytes("0 0 10 10 re f 10 10 10 10 re f"))]));

        yield return ("form-xobject-icc", Build("/ColorSpace << /Cs1 6 0 R >> /XObject << /Fm 8 0 R >>",
            "q 1 0 0 1 20 150 cm /Fm Do Q " + text,
            [.. Icc(srgb), new("<< /Type /XObject /Subtype /Form /BBox [0 0 300 120] /Resources << /ColorSpace << /Cs1 6 0 R >> /Font << /F1 5 0 R >> >> >>",
                Encoding.Latin1.GetBytes("/Cs1 cs 0.9 0.5 0.1 sc 0 0 300 120 re f /Cs1 cs 0 0 0 sc BT /F1 20 Tf 10 50 Td (in form) Tj ET"))]));

        yield return ("quartz-like-white-page", Build("/ColorSpace << /Cs1 6 0 R >>",
            "q Q q 0 0 400 400 re W n /Cs1 cs 1 1 1 sc 0 0 400 400 re f /Cs1 cs 0.1 0.1 0.6 sc " + text + "/Cs1 CS 0.8 0.2 0.2 SC 2 w 20 100 360 80 re S Q",
            Icc(srgb)));
    }

    private (int Diff, int Total) Compare(string a, string b)
    {
        return _t.InvokeAsync(() =>
        {
            byte[] Render(string path)
            {
                using var d = PdfDocument.Open(_t, path, null);
                using var p = d.LoadPage(0);
                return p.RenderTile(1.0, 0, 0, 400, 400, true);
            }
            var x = Render(a); var y = Render(b);
            if (Environment.GetEnvironmentVariable("V13X_KEEP") is not null)
            { PdfEditor.TestFixtures.PngFile.WriteBgra(a + ".png", x, 400, 400); PdfEditor.TestFixtures.PngFile.WriteBgra(b + ".png", y, 400, 400); }
            int diff = 0;
            for (int i = 0; i < x.Length; i += 4)
                if (Math.Abs(x[i] - y[i]) > 8 || Math.Abs(x[i + 1] - y[i + 1]) > 8 || Math.Abs(x[i + 2] - y[i + 2]) > 8) diff++;
            return (diff, x.Length / 4);
        }).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task EditedPage_SaveKeepsLook_ForEveryColorSpaceAndPaintKind()
    {
        Directory.CreateDirectory(_dir);
        var report = new List<string>();
        var failures = new List<string>();
        var guarded = new List<string>();
        foreach (var (name, pdf) in Cases())
        {
            string src = Path.Combine(_dir, name + ".pdf"), outp = Path.Combine(_dir, name + ".out.pdf");
            File.WriteAllBytes(src, pdf);
            await using var s = await DocumentSession.OpenAsync(_t, src, null);
            var idx = await _t.InvokeAsync(() => s.GetObjectIndex(s.Pages[0].Id).Entries.ToList());
            var target = idx.FirstOrDefault(e => e.Type == PdfPageObjectType.Text);
            if (target is null) { failures.Add(name + ": no text object"); continue; }
            // 아무것도 움직이지 않는 이동 — 쪽 내용이 GenerateContent로 다시 쓰이게만 한다
            await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, [new TransformEdit(s.Content.NewId(), target.Ref, PdfMatrix.Translate(0, 0))]));
            try { await new SaveService([], null, null, null).SaveAsync(s, outp); }
            catch (Exception e) when (e is ContentRegenerationException || e.InnerException is ContentRegenerationException)
            {
                guarded.Add(name);   // 모양이 달라질 파일을 내지 않고 막았다
                report.Add($"{name}: guarded");
                continue;
            }
            var (diff, total) = Compare(src, outp);
            report.Add($"{name}: {diff}/{total} ({100.0 * diff / total:F2}%)");
            if (diff > total * 0.002) failures.Add($"{name}: {diff}/{total}");
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "v13x-report.txt"), report);
        Assert.True(failures.Count == 0, string.Join("; ", failures) + "\n" + string.Join("\n", report));
    }
}

/// <summary>
/// 실제 PDF(저장소에 없는 private/ 폴더, 있을 때만)의 앞 3쪽에 아무것도 안 움직이는 편집을 해 저장했을 때 충실도 보호가 어느 쪽을 막는지 임시 폴더에 보고한다.
/// 이름·내용은 기록하지 않는다.
/// </summary>
public sealed class RealPdfGuardTests : IDisposable
{
    private readonly PdfiumThread _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task PrivatePdfs_IdentityEditOnFirstPages_ReportsWhichPagesTheGuardBlocks()
    {
        string? dir = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "private"))) { dir = Path.Combine(d.FullName, "private"); break; }
        if (dir is null) return;
        var report = new List<string>();
        int n = 0;
        foreach (var file in Directory.GetFiles(dir, "*.pdf"))
        {
            n++;
            string outp = Path.Combine(Path.GetTempPath(), "real-guard-" + Guid.NewGuid().ToString("N") + ".pdf");
            try
            {
                await using var s = await DocumentSession.OpenAsync(_t, file, null);
                var edits = new List<(PageId Id, ObjectRef Ref)>();
                for (int p = 0; p < Math.Min(3, s.Pages.Length); p++)
                {
                    var id = s.Pages[p].Id;
                    var idx = await _t.InvokeAsync(() => s.GetObjectIndex(id).Entries.ToList());
                    var e = idx.FirstOrDefault(x => x.Type == PdfPageObjectType.Path) ?? idx.FirstOrDefault(x => x.Type == PdfPageObjectType.Text);
                    if (e is not null) edits.Add((id, e.Ref));
                }
                foreach (var (id, r) in edits)
                    await s.Commands.ExecuteAsync(new ContentEditCommand(id, [new TransformEdit(s.Content.NewId(), r, PdfMatrix.Translate(0, 0))]));
                await new SaveService([], null, null, null).SaveAsync(s, outp);
                report.Add($"file{n}: ok ({edits.Count} pages)");
            }
            catch (Exception e) when (e is ContentRegenerationException || e.InnerException is ContentRegenerationException)
            {
                report.Add($"file{n}: BLOCKED {(e as ContentRegenerationException ?? (ContentRegenerationException)e.InnerException!).PageNumber}");
            }
            finally { try { File.Delete(outp); } catch { } }
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "v13x-real-report.txt"), report);
        // 보고만 한다(실제 PDF는 저장소에 없고, 막히는 쪽은 PDFium 다시 쓰기의 한계 — 결과 문서 "M4 마무리 검토").
        Assert.Equal(n, report.Count);
    }
}
