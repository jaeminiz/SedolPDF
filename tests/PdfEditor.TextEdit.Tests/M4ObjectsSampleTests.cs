using System.Runtime.CompilerServices;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;

namespace PdfEditor.TextEdit.Tests;

/// <summary>
/// 다른 뷰어에서 열어 볼 M4 호환성 샘플을 만든다(docs/checklists/viewer-compat.md M4 절).
/// `spikes/out/m4-objects-document.pdf`(일반 문서형 A4)와 `m4-objects-drawing.pdf`(도면형 A3 가로): 원래 글꼴로 바꾼 줄, 앱 글꼴로 바뀐 한글 줄,
/// 옮긴 이미지, 지운 도형, 옮긴 폼(묶음), 새 이미지·도형·텍스트. 실제 저장 길(편집 기록 → 출력 사본 → PDFsharp 패치)로 저장한 뒤 다시 열어 자동 확인한다.
/// 다른 뷰어에서의 모양은 사람이 확인한다(검증 필요).
/// </summary>
public sealed class M4ObjectsSampleTests : IDisposable
{
    private static readonly Lazy<AppFont> Font = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        return AppFont.Load(Path.Combine(dir!.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"));
    });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-m4sample-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public M4ObjectsSampleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string OutDir([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "spikes", "out"));

    private static byte[] SamplePng() => TestImages.Png(80, 50, (x, y) => ((byte)(x * 3), (byte)(255 - y * 5), 90, 255));

    private async Task<ReplacePlan> PlanAsync(DocumentSession s, ObjectRef target, string text) =>
        await _t.InvokeAsync(() => TextEditPlanner.PlanReplace(s, s.Pages[0].Id, target, text, Font.Value));

    private static async Task RunAsync(DocumentSession s, params ContentEdit[] edits) =>
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, edits));

    private static PageObjectEntry Pick(DocumentSession s, Func<PageObjectEntry, bool> f) => s.GetObjectIndex(s.Pages[0].Id).Entries.First(f);

    private async Task<PageObjectEntry> PickAsync(DocumentSession s, Func<PageObjectEntry, bool> f) => await _t.InvokeAsync(() => Pick(s, f));

    private async Task AddNewAsync(DocumentSession s, float imgX, float imgY, string text, float textX, float textY)
    {
        string asset = s.Assets.AddImage(SamplePng());
        var img = new InsertImageEdit(s.Content.NewId(), asset, new PdfMatrix(160, 0, 0, 100, imgX, imgY));
        var shape = new InsertShapeEdit(s.Content.NewId(), new ShapeSpec(ShapeKind.Ellipse, new PdfRectF(imgX + 180, imgY, imgX + 320, imgY + 80), default, default,
            new ShapeStyle(new PdfColor(0x1A, 0x6B, 0xD1), 2f, new PdfColor(0xFF, 0xE0, 0x80))));
        var (spec, missing) = AppFontLine.Build(Font.Value, text, 14, PdfMatrix.Translate(textX, textY), new PdfColor(0x14, 0x96, 0x46));
        Assert.NotNull(spec);
        Assert.True(missing.IsDefaultOrEmpty);
        await RunAsync(s, img, shape, new InsertAppTextEdit(s.Content.NewId(), spec!));
    }

    private (string Text, int ImageCount) Inspect(string path)
    {
        return _t.InvokeAsync(() =>
        {
            using var d = PdfDocument.Open(_t, path, null);
            using var p = d.LoadPage(0);
            using var tp = p.LoadText();
            int images = Enumerable.Range(0, p.ObjectCount).Count(i => p.GetObject(i).Type == PdfPageObjectType.Image);
            return (tp.GetText(0, tp.CharCount), images);
        }).GetAwaiter().GetResult();
    }

    private void WritePng(string pdf, string png)
    {
        var (bgra, w, h) = _t.InvokeAsync(() =>
        {
            using var d = PdfDocument.Open(_t, pdf, null);
            using var p = d.LoadPage(0);
            var size = p.SizePt;
            int pw = (int)Math.Ceiling(size.Width), ph = (int)Math.Ceiling(size.Height);
            return (p.RenderTile(1.0, 0, 0, pw, ph, true), pw, ph);
        }).GetAwaiter().GetResult();
        PngFile.WriteBgra(png, bgra, w, h);
    }

    [Fact]
    public async Task Sample_DocumentLike_SavedAndReopened()
    {
        string src = Path.Combine(_dir, "doc.pdf");
        ContentPdfFactory.CreateDocumentLike(src);
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        var page = s.Pages[0].Id;

        // ① 원래 글꼴로 바꾼 줄
        var arial = await PickAsync(s, e => e.Type == PdfPageObjectType.Text && (e.TextPreview ?? "").StartsWith("Arial"));
        var p1 = await PlanAsync(s, arial.Ref, "Arial 321 CBA");
        Assert.False(p1.FallbackUsed);
        await RunAsync(s, [.. p1.Edits]);
        // ② 앱 글꼴로 바뀐 한글 줄(서브셋에 없는 글자)
        var kor = await PickAsync(s, e => e.Type == PdfPageObjectType.Text && (e.TextPreview ?? "").StartsWith("가나다"));
        var p2 = await PlanAsync(s, kor.Ref, "가나다 도면 승인 완료");
        Assert.True(p2.FallbackUsed);
        await RunAsync(s, [.. p2.Edits]);
        // ③ 옮긴 이미지, ④ 지운 사각형
        var image = await PickAsync(s, e => e.Type == PdfPageObjectType.Image);
        await RunAsync(s, new TransformEdit(s.Content.NewId(), image.Ref, PdfMatrix.Translate(300, -60)));
        var rect = await PickAsync(s, e => e.Type == PdfPageObjectType.Path && Math.Abs(e.Bounds.Width - 150) < 12 && Math.Abs(e.Bounds.Height - 90) < 12);
        await RunAsync(s, new DeactivateEdit(s.Content.NewId(), rect.Ref));
        // ⑤ 새 이미지·도형·텍스트
        await AddNewAsync(s, 60, 300, "새 텍스트 NEW 123", 60, 280);

        string outPath = Path.Combine(OutDir(), "m4-objects-document.pdf");
        Directory.CreateDirectory(OutDir());
        await new SaveService([], null, new PdfSharpAnnotationPatcher(Font.Value), s.Assets).SaveAsync(s, outPath);

        var (text, images) = Inspect(outPath);
        Assert.Contains("Arial 321 CBA", text);
        Assert.Contains("가나다 도면 승인 완료", text);
        Assert.Contains("새 텍스트 NEW 123", text);
        Assert.DoesNotContain(ContentPdfFactory.LineArial, text);
        Assert.Equal(2, images);   // 옮긴 원래 이미지 + 새 이미지
        WritePng(outPath, Path.Combine(OutDir(), "m4-objects-document.png"));
    }

    [Fact]
    public async Task Sample_DrawingLike_SavedAndReopened()
    {
        string src = Path.Combine(_dir, "drawing.pdf");
        ContentPdfFactory.CreateDrawingLike(src);
        await using var s = await DocumentSession.OpenAsync(_t, src, null);

        // 표제란 글(쪽 수준 텍스트)을 바꾸고, 폼(묶음) 전체를 옮기고, 새 이미지·도형·텍스트를 넣는다
        var title = await PickAsync(s, e => e.Type == PdfPageObjectType.Text);
        var plan = await PlanAsync(s, title.Ref, "TITLE BLOCK 표제란 개정 B");
        Assert.NotEmpty(plan.Edits);
        await RunAsync(s, [.. plan.Edits]);
        var form = await PickAsync(s, e => e.Type == PdfPageObjectType.Form);
        await RunAsync(s, new TransformEdit(s.Content.NewId(), form.Ref, PdfMatrix.Translate(40, -40)));
        await AddNewAsync(s, 700, 600, "도면 검토 NEW 456", 700, 580);

        string outPath = Path.Combine(OutDir(), "m4-objects-drawing.pdf");
        await new SaveService([], null, new PdfSharpAnnotationPatcher(Font.Value), s.Assets).SaveAsync(s, outPath);

        var (text, images) = Inspect(outPath);
        Assert.Contains("개정 B", text);
        Assert.Contains("도면 검토 NEW 456", text);
        Assert.Equal(1, images);
        WritePng(outPath, Path.Combine(OutDir(), "m4-objects-drawing.png"));
    }
}
