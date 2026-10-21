using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document;
using PdfEditor.Document.Content;
using PdfEditor.Document.Output;
using PdfEditor.Document.Saving;

namespace PdfEditor.TextEdit.Tests;

/// <summary>
/// M4x Task 7: PDFium이 다시 쓰지 못하는 쪽(셰이딩·패턴·이미지 마스크)은 내용 스트림 직접 고쳐 쓰기로 저장되고, 편집하지 않은 부분은 그대로여야 한다.
/// </summary>
public sealed class ContentRewriteSaveTests : IDisposable
{
    private static readonly Lazy<AppFont> Font = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        return AppFont.Load(Path.Combine(dir!.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"));
    });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-rw-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    public ContentRewriteSaveTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { _t.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    private static readonly string[] Limits = ["shading-sh", "shading-pattern-fill", "tiling-pattern-colored", "tiling-pattern-uncolored", "image-mask-icc-fill"];

    private SaveService Svc(DocumentSession s) => new([], null, new PdfSharpAnnotationPatcher(Font.Value), s.Assets);

    private int Diff(string a, string b, PdfRectF? skip)
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
            int diff = 0;
            for (int py = 0; py < 400; py++)
                for (int px = 0; px < 400; px++)
                {
                    if (skip is { } r && px >= r.Left - 4 && px <= r.Right + 4 && 400 - py >= r.Bottom - 4 && 400 - py <= r.Top + 4) continue;
                    int i = (py * 400 + px) * 4;
                    if (Math.Abs(x[i] - y[i]) > 8 || Math.Abs(x[i + 1] - y[i + 1]) > 8 || Math.Abs(x[i + 2] - y[i + 2]) > 8) diff++;
                }
            return diff;
        }).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task KnownPdfiumLimits_SaveThroughContentRewrite_NoLookChange()
    {
        var report = new List<string>();
        foreach (var (name, pdf) in ColorSpaceFidelityTests.Cases().Where(c => Limits.Contains(c.Name)))
        {
            string src = Path.Combine(_dir, name + ".pdf"), idOut = Path.Combine(_dir, name + ".id.pdf"), delOut = Path.Combine(_dir, name + ".del.pdf");
            File.WriteAllBytes(src, pdf);

            // ① 제자리 이동(내용만 다시 쓰이게): 전체가 같아야 한다
            await using (var s = await DocumentSession.OpenAsync(_t, src, null))
            {
                var idx = await _t.InvokeAsync(() => s.GetObjectIndex(s.Pages[0].Id).Entries.ToList());
                var target = idx.First(e => e.Type == PdfPageObjectType.Text);
                await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, [new TransformEdit(s.Content.NewId(), target.Ref, PdfMatrix.Translate(0, 0))]));
                await Svc(s).SaveAsync(s, idOut);
            }
            int d1 = Diff(src, idOut, null);

            // ② 글 객체 지우기: 지운 자리 말고는 같아야 한다
            PdfRectF bounds;
            await using (var s = await DocumentSession.OpenAsync(_t, src, null))
            {
                var idx = await _t.InvokeAsync(() => s.GetObjectIndex(s.Pages[0].Id).Entries.ToList());
                var target = idx.First(e => e.Type == PdfPageObjectType.Text);
                bounds = target.Bounds;
                await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, [new DeactivateEdit(s.Content.NewId(), target.Ref)]));
                await Svc(s).SaveAsync(s, delOut);
            }
            int d2 = Diff(src, delOut, bounds);
            report.Add($"{name}: identity {d1} px, delete(outside box) {d2} px");
            Assert.True(d1 <= 160 && d2 <= 160, string.Join("\n", report));
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "m4x-rewrite-report.txt"), report);
    }

    [Fact]
    public async Task Shading_Survives_ContentBytes_AreKept()
    {
        var pdf = ColorSpaceFidelityTests.Cases().First(c => c.Name == "shading-sh").Pdf;
        string src = Path.Combine(_dir, "sh.pdf"), outp = Path.Combine(_dir, "sh.out.pdf");
        File.WriteAllBytes(src, pdf);
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        var idx = await _t.InvokeAsync(() => s.GetObjectIndex(s.Pages[0].Id).Entries.ToList());
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, [new DeactivateEdit(s.Content.NewId(), idx.First(e => e.Type == PdfPageObjectType.Text).Ref)]));
        await Svc(s).SaveAsync(s, outp);
        using var pdfDoc = PdfSharp.Pdf.IO.PdfReader.Open(outp, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        string text = System.Text.Encoding.Latin1.GetString(PdfEditor.ContentStream.PageContent.Load(pdfDoc.Pages[0]).Data!);
        Assert.Contains("/Sh1 sh", text);   // 셰이딩 연산자가 그대로 남아 있다(PDFium 다시 쓰기는 이것을 지운다)
    }

    [Fact]
    public async Task Rewrite_Adds_NewShape_AndMovesOriginal_OnShadingPage()
    {
        var pdf = ColorSpaceFidelityTests.Cases().First(c => c.Name == "shading-pattern-fill").Pdf;
        string src = Path.Combine(_dir, "sp.pdf"), outp = Path.Combine(_dir, "sp.out.pdf");
        File.WriteAllBytes(src, pdf);
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        var idx = await _t.InvokeAsync(() => s.GetObjectIndex(s.Pages[0].Id).Entries.ToList());
        var shape = new ShapeSpec(ShapeKind.Rectangle, new PdfRectF(30, 30, 120, 90), default, default, new ShapeStyle(new PdfEditor.Document.Annotations.PdfColor(200, 0, 0), 3, new PdfEditor.Document.Annotations.PdfColor(255, 255, 0)));
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, [
            new InsertShapeEdit(s.Content.NewId(), shape),
            new TransformEdit(s.Content.NewId(), idx.First(e => e.Type == PdfPageObjectType.Text).Ref, PdfMatrix.Translate(40, -20)),
        ]));
        await Svc(s).SaveAsync(s, outp);
        // 다시 열어 새 도형이 있고 객체 수가 맞는지(글 옮기기 = 원본 숨김 + 사본 1개)
        int count = await _t.InvokeAsync(() => { using var d = PdfDocument.Open(_t, outp, null); using var p = d.LoadPage(0); return p.ObjectCount; });
        Assert.True(count >= idx.Count + 1);
    }

    [Fact]
    public async Task EncryptedDocument_StillBlocked_WhenPdfiumCannotRewrite()
    {
        // 암호 문서는 패치 단계가 없다(U-B): 보호가 막는 지금 동작을 유지한다. 암호를 거는 도구가 없으므로 CanPatch=false 경로는 단위로 확인하지 않고,
        // 패처가 없는 SaveService(= 고쳐 쓰기 불가)로 같은 분기를 본다.
        var pdf = ColorSpaceFidelityTests.Cases().First(c => c.Name == "shading-sh").Pdf;
        string src = Path.Combine(_dir, "enc.pdf"), outp = Path.Combine(_dir, "enc.out.pdf");
        File.WriteAllBytes(src, pdf);
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        var idx = await _t.InvokeAsync(() => s.GetObjectIndex(s.Pages[0].Id).Entries.ToList());
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, [new DeactivateEdit(s.Content.NewId(), idx.First(e => e.Type == PdfPageObjectType.Text).Ref)]));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => new SaveService([], null, null, s.Assets).SaveAsync(s, outp));
        Assert.True(ex is ContentRegenerationException || ex.InnerException is ContentRegenerationException, ex.ToString());
    }
}
