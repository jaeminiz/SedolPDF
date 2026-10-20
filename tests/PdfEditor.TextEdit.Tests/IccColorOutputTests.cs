using System.Text;
using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Content;
using PdfEditor.Document.Saving;


namespace PdfEditor.TextEdit.Tests;

/// <summary>
/// 2026-10-20 사용자 보고: macOS Quartz PDF(ICCBased 색 공간, 쪽 전체 흰 배경 사각형)에서 객체를 편집·저장하면 쪽 배경이 검정이 되고 글자가 안 보였다.
/// PDFium GenerateContent가 ICCBased 색을 다시 쓰지 못한 것 → 출력 사본에서 색을 RGB로 다시 지정한다(ContentReplayer.NormalizeColors).
/// </summary>
public sealed class IccColorOutputTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-quartz-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    public void Dispose() { _t.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    private static byte[] Build(byte[] icc, string content)
    {
        var ms = new MemoryStream();
        var offs = new List<long>();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b); }
        void Obj(int n, Action body) { offs.Add(ms.Position); W($"{n} 0 obj\n"); body(); W("endobj\n"); }
        W("%PDF-1.4\n");
        Obj(1, () => W("<< /Type /Catalog /Pages 2 0 R >>\n"));
        Obj(2, () => W("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\n"));
        Obj(3, () => W("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /ColorSpace << /Cs1 5 0 R >> /Font << /F1 7 0 R >> >> >>\n"));
        Obj(4, () => { var c = Encoding.Latin1.GetBytes(content); W($"<< /Length {c.Length} >>\nstream\n"); ms.Write(c); W("\nendstream\n"); });
        Obj(5, () => W("[/ICCBased 6 0 R]\n"));
        Obj(6, () => { W($"<< /N 3 /Alternate /DeviceRGB /Length {icc.Length} >>\nstream\n"); ms.Write(icc); W("\nendstream\n"); });
        Obj(7, () => W("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\n"));
        long xref = ms.Position;
        W($"xref\n0 {offs.Count + 1}\n0000000000 65535 f \n");
        foreach (var of in offs) W($"{of:D10} 00000 n \n");
        W($"trailer\n<< /Size {offs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private (int Dark, int White) Count(string path)
    {
        return _t.InvokeAsync(() =>
        {
            using var d = PdfDocument.Open(_t, path, null);
            using var p = d.LoadPage(0);
            var b = p.RenderTile(0.5, 0, 0, 298, 421, true);
            int dark = 0, white = 0;
            for (int i = 0; i < b.Length; i += 4) { if (b[i] < 40 && b[i + 1] < 40 && b[i + 2] < 40) dark++; else if (b[i] > 240) white++; }
            return (dark, white);
        }).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task EditedPage_IccBasedColors_KeepTheirLook()
    {
        Directory.CreateDirectory(_dir);
        var icc = File.ReadAllBytes(@"C:\Windows\System32\spool\drivers\color\AdobeRGB1998.icc");
        string content = "q Q q 0 0 595 842 re W n /Cs1 cs 1 1 1 sc 0 0 595 842 re f\n" +
            "/Cs1 cs 0.1 0.1 0.6 sc BT /F1 24 Tf 50 700 Td (Hello world) Tj ET\n" +
            "/Cs1 cs 0 0 0 sc BT /F1 24 Tf 50 600 Td (Second line) Tj ET\nQ\n";
        string src = Path.Combine(_dir, "q.pdf");
        File.WriteAllBytes(src, Build(icc, content));
        var before = Count(src);
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        var idx = await _t.InvokeAsync(() => s.GetObjectIndex(s.Pages[0].Id).Entries.ToList());
        var text = idx.First(e => e.Type == PdfPageObjectType.Text);
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, [new TransformEdit(s.Content.NewId(), text.Ref, PdfMatrix.Translate(0, -50))]));
        string outp = Path.Combine(_dir, "o.pdf");
        await new SaveService([], null, null, null).SaveAsync(s, outp);
        var after = Count(outp);
        Assert.Equal(before.White, after.White);   // 흰 배경이 그대로(검정이 되지 않음)
        Assert.True(after.Dark <= before.Dark + 400, $"dark {before.Dark} -> {after.Dark}");
    }
}

public sealed class SubsetGlyphRuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-subset-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    public void Dispose() { _t.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task EmbeddedFont_OnlyCharsOfTheOriginalLine_AreAllowed()
    {
        Directory.CreateDirectory(_dir);
        string p = Path.Combine(_dir, "d.pdf");
        PdfEditor.TestFixtures.ContentPdfFactory.CreateDocumentLike(p);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        var (arial, helv) = await _t.InvokeAsync(() =>
        {
            var e = s.GetObjectIndex(s.Pages[0].Id).Entries.Where(x => x.Type == PdfPageObjectType.Text).ToList();
            return (e.First(x => (x.TextPreview ?? "").StartsWith("Arial")).Ref.OriginalIndex, e.First(x => (x.TextPreview ?? "").StartsWith("Helvetica")).Ref.OriginalIndex);
        });
        // 임베드 서브셋: 줄에 있던 글자만 → 통과, 없던 글자(Z)가 섞이면 앱 글꼴로
        Assert.Equal(OriginalFontReplacer.Result.Ok, await _t.InvokeAsync(() => OriginalFontReplacer.Verify(s, 0, arial, "Arial 321 CBA")));
        Assert.Equal(OriginalFontReplacer.Result.GlyphNotInSubset, await _t.InvokeAsync(() => OriginalFontReplacer.Verify(s, 0, arial, "Arial Zebra")));
        // 임베드 안 된 표준 글꼴은 아무 글자나
        Assert.Equal(OriginalFontReplacer.Result.Ok, await _t.InvokeAsync(() => OriginalFontReplacer.Verify(s, 0, helv, "Zebra Quiz 789")));
    }
}
