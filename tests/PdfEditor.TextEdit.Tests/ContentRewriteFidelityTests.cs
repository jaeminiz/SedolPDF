using System.Text;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.Document.Output;
using PdfEditor.TestFixtures;

namespace PdfEditor.TextEdit.Tests;

/// <summary>
/// M4x Task 8: 내용 스트림 고쳐 쓰기(ForceContentRewrite)를 모든 합성 샘플·글자 간격 글·실제 PDF(private/ 있을 때만)에 강제로 써서
/// 편집 → 저장 → 렌더가 편집 밖 부분과 다르지 않은지 본다. 저장 중 충실도 보호(메모리 모양 ≡ 저장 모양)가 어긋나면 ContentRegenerationException이 나므로 예외가 없으면 통과다.
/// </summary>
public sealed class ContentRewriteFidelityTests : IDisposable
{
    private static readonly Lazy<AppFont> Font = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        return AppFont.Load(Path.Combine(dir!.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"));
    });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-rwf-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    public ContentRewriteFidelityTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { _t.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    private OutputCopyBuilder Builder(DocumentSession s) => new(null, new PdfSharpAnnotationPatcher(Font.Value), s.Assets) { ForceContentRewrite = true };

    private int Diff(string a, string b, int page, PdfRectF? skip, int w = 400, int h = 400)
    {
        return _t.InvokeAsync(() =>
        {
            byte[] Render(string path)
            {
                using var d = PdfDocument.Open(_t, path, null);
                using var p = d.LoadPage(page);
                return p.RenderTile(1.0, 0, 0, w, h, true);
            }
            var x = Render(a); var y = Render(b);
            int diff = 0;
            for (int py = 0; py < h; py++)
                for (int px = 0; px < w; px++)
                {
                    if (skip is { } r && px >= r.Left - 4 && px <= r.Right + 4 && h - py >= r.Bottom - 4 && h - py <= r.Top + 4) continue;
                    int i = (py * w + px) * 4;
                    if (Math.Abs(x[i] - y[i]) > 8 || Math.Abs(x[i + 1] - y[i + 1]) > 8 || Math.Abs(x[i + 2] - y[i + 2]) > 8) diff++;
                }
            return diff;
        }).GetAwaiter().GetResult();
    }

    private static IEnumerable<(string Name, byte[] Pdf)> AllCases()
    {
        foreach (var c in ColorSpaceFidelityTests.Cases()) yield return c;
        // 글자 간격(Tc/Tw)이 있는 글 — PDFium 다시 쓰기가 깨뜨린 종류
        yield return ("char-spacing", ColorSpaceFidelityTests.Build("",
            "BT /F1 14 Tf 3 Tc 12 Tw 20 300 Td (Hello big spaced world) Tj ET BT /F1 18 Tf 1.5 Tc 20 250 Td [(AB) -300 (CD) 120 (E F)] TJ ET 20 100 160 80 re S", []));
    }

    [Fact]
    public async Task EveryCase_EditsThroughRewrite_KeepLookOutsideTheEdit()
    {
        var report = new List<string>();
        var failures = new List<string>();
        foreach (var (name, pdf) in AllCases())
        {
            string src = Path.Combine(_dir, name + ".pdf");
            File.WriteAllBytes(src, pdf);

            // ① 글 지우기: 지운 자리 말고는 원본과 같아야 한다
            string delOut = Path.Combine(_dir, name + ".del.pdf");
            PdfRectF bounds;
            await using (var s = await DocumentSession.OpenAsync(_t, src, null))
            {
                var idx = await _t.InvokeAsync(() => s.GetObjectIndex(s.Pages[0].Id).Entries.ToList());
                var target = idx.First(e => e.Type == PdfPageObjectType.Text);
                bounds = target.Bounds;
                await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, [new DeactivateEdit(s.Content.NewId(), target.Ref)]));
                await Builder(s).BuildAsync(s, delOut);
            }
            int d1 = Diff(src, delOut, 0, bounds);

            // ② 글 옮기기 + 새 도형 + 새 이미지 + 원본 도형 지우기, 저장 → 다시 열어 같은 방식으로 한 번 더(왕복)
            string mixOut = Path.Combine(_dir, name + ".mix.pdf"), twice = Path.Combine(_dir, name + ".twice.pdf");
            await using (var s = await DocumentSession.OpenAsync(_t, src, null))
            {
                var idx = await _t.InvokeAsync(() => s.GetObjectIndex(s.Pages[0].Id).Entries.ToList());
                var text = idx.First(e => e.Type == PdfPageObjectType.Text);
                string asset = s.Assets.AddImage(TestImages.Png(40, 30, (x, y) => ((byte)(x * 6), (byte)(y * 8), 120, 255)));
                var edits = new List<ContentEdit>
                {
                    new TransformEdit(s.Content.NewId(), text.Ref, PdfMatrix.Translate(30, -40)),
                    new InsertShapeEdit(s.Content.NewId(), new ShapeSpec(ShapeKind.Ellipse, new PdfRectF(250, 20, 380, 90), default, default, new ShapeStyle(new PdfColor(200, 30, 30), 3, new PdfColor(255, 230, 120)))),
                    new InsertImageEdit(s.Content.NewId(), asset, new PdfMatrix(80, 0, 0, 60, 280, 320)),
                };
                if (idx.FirstOrDefault(e => e.Type == PdfPageObjectType.Path && !e.IsFrame) is { } path) edits.Add(new DeactivateEdit(s.Content.NewId(), path.Ref));
                await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, edits));
                try { await Builder(s).BuildAsync(s, mixOut); }
                catch (Exception ex) { failures.Add($"{name}: mixed save failed: {ex.Message}"); continue; }
            }
            await using (var s = await DocumentSession.OpenAsync(_t, mixOut, null))
            {
                var idx = await _t.InvokeAsync(() => s.GetObjectIndex(s.Pages[0].Id).Entries.ToList());
                var text = idx.First(e => e.Type == PdfPageObjectType.Text);
                await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, [new TransformEdit(s.Content.NewId(), text.Ref, PdfMatrix.Translate(-10, 5))]));
                await Builder(s).BuildAsync(s, twice);
            }
            report.Add($"{name}: delete outside box {d1} px; mixed edit + reopen/edit saved");
            if (d1 > 160) failures.Add($"{name}: {d1}");
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "m4x-fidelity-report.txt"), report);
        Assert.True(failures.Count == 0, string.Join("; ", failures) + "\n" + string.Join("\n", report));
    }

    [Fact]
    public async Task FactoryDocuments_DocumentAndDrawing_RewriteKeepsLook()
    {
        foreach (var kind in new[] { "doc", "drawing" })
        {
            string src = Path.Combine(_dir, kind + ".pdf"), outp = Path.Combine(_dir, kind + ".out.pdf");
            if (kind == "doc") ContentPdfFactory.CreateDocumentLike(src); else ContentPdfFactory.CreateDrawingLike(src);
            await using var s = await DocumentSession.OpenAsync(_t, src, null);
            var size = await _t.InvokeAsync(() => { using var d = PdfDocument.Open(_t, src, null); using var p = d.LoadPage(0); return p.SizePt; });
            var idx = await _t.InvokeAsync(() => s.GetObjectIndex(s.Pages[0].Id).Entries.ToList());
            var target = idx.First(e => e.Type == PdfPageObjectType.Text);
            await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, [new DeactivateEdit(s.Content.NewId(), target.Ref)]));
            await Builder(s).BuildAsync(s, outp);
            int w = (int)Math.Ceiling(size.Width), h = (int)Math.Ceiling(size.Height);
            Assert.True(Diff(src, outp, 0, target.Bounds, w, h) <= w * h / 2000, kind);
        }
    }

    [Fact]
    public async Task AppFontLine_OnRewrittenPage_IsSavedWithRealGlyphs()
    {
        var pdf = ColorSpaceFidelityTests.Cases().First(c => c.Name == "shading-sh").Pdf;
        string src = Path.Combine(_dir, "app.pdf"), outp = Path.Combine(_dir, "app.out.pdf");
        File.WriteAllBytes(src, pdf);
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        var (spec, missing) = AppFontLine.Build(Font.Value, "새 텍스트 NEW 123", 16, PdfMatrix.Translate(40, 200), new PdfColor(0x14, 0x96, 0x46));
        Assert.NotNull(spec);
        var line = new InsertAppTextEdit(s.Content.NewId(), spec!);
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, [line, new TransformEdit(s.Content.NewId(), ObjectRef.Inserted(line.Id), PdfMatrix.Translate(20, 10))]));
        await Builder(s).BuildAsync(s, outp);   // 충실도 보호가 통과해야 한다(화면의 윤곽선 ≡ 저장한 글자)
        string text = await _t.InvokeAsync(() => { using var d = PdfDocument.Open(_t, outp, null); using var p = d.LoadPage(0); using var tp = p.LoadText(); return tp.GetText(0, tp.CharCount); });
        Assert.Contains("새 텍스트 NEW 123", text);
    }

    [Fact]
    public async Task PrivatePdfs_ForcedRewrite_OnFirstPages_ReportsOutcome()
    {
        string? dir = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "private"))) { dir = Path.Combine(d.FullName, "private"); break; }
        if (dir is null) return;
        var report = new List<string>();
        int n = 0, ok = 0, mapRefused = 0, lookRefused = 0, other = 0;
        foreach (var file in Directory.GetFiles(dir, "*.pdf"))
        {
            n++;
            for (int p = 0; p < 3; p++)
            {
                string outp = Path.Combine(_dir, $"real{n}-{p}.pdf");
                try
                {
                    await using var s = await DocumentSession.OpenAsync(_t, file, null);
                    if (p >= s.Pages.Length) break;
                    var id = s.Pages[p].Id;
                    var idx = await _t.InvokeAsync(() => s.GetObjectIndex(id).Entries.ToList());
                    var path = idx.FirstOrDefault(x => x.Type == PdfPageObjectType.Path && !x.IsFrame);
                    var text = idx.FirstOrDefault(x => x.Type == PdfPageObjectType.Text);
                    var edits = new List<ContentEdit>();
                    if (path is not null) edits.Add(new DeactivateEdit(s.Content.NewId(), path.Ref));
                    if (text is not null) edits.Add(new TransformEdit(s.Content.NewId(), text.Ref, PdfMatrix.Translate(12, -9)));
                    if (edits.Count == 0) continue;
                    await s.Commands.ExecuteAsync(new ContentEditCommand(id, edits));
                    await Builder(s).BuildAsync(s, outp);
                    ok++;
                }
                catch (ContentRegenerationException e)
                {
                    if (e.Reason is { } r && (r.Contains("object count") || r.Contains("kind ") || r.Contains("bounds differ") || r.Contains("origin far"))) mapRefused++;
                    else if (e.Reason == "content-stream rewrite changed the look") lookRefused++;
                    else other++;
                    report.Add($"file{n} page{p + 1}: REFUSED ({e.Reason})");
                }
                finally { try { File.Delete(outp); } catch { } }
            }
        }
        report.Add($"summary: ok {ok}, map refused {mapRefused}, look refused {lookRefused}, other {other}");
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "m4x-real-report.txt"), report);
        Assert.Equal(0, lookRefused);   // 충실도 보호가 막은 쪽은 없어야 한다(대응 실패로 안전하게 거절하는 것은 허용)
    }
}
