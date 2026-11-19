using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;

namespace PdfEditor.Document.Tests;

/// <summary>0.14.0 안 C: 주석을 건드리지 않은 쪽은 저장마다 PDFium으로 다시 불러 세지 않는다. 건드린 쪽은 예전처럼 센다.</summary>
public class AnnotCountSkipTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-annotskip-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public AnnotCountSkipTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static Annotation NewInk(DocumentSession s) => new()
    {
        Id = s.Annotations.NewId(),
        Kind = AnnotKind.Ink,
        Rect = new PdfRectF(50, 50, 150, 120),
        Color = new PdfColor(229, 55, 45),
        BorderWidth = 2f,
        InkStrokes = [[new PdfPointF(60, 60), new PdfPointF(100, 110), new PdfPointF(140, 70)]],
        Name = Guid.NewGuid().ToString("D"),
        Flags = PdfAnnotFlags.Print,
    };

    private Task<int?> Pristine(DocumentSession s, int pageIndex) => _t.InvokeAsync(() => s.PristineAnnotCount(s.PageIdAt(pageIndex)));

    private static SaveService Svc() => new([], new PdfEditor.Appearance.PdfAppearanceSource());

    [Fact]
    public async Task UntouchedPages_AreCountedAtOpen_TouchedPageIsNot()
    {
        var p = Path.Combine(_dir, "a.pdf");
        TestPdfFactory.CreateBlank(p, 3, 595, 842);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        await s.AnnotationsReady;
        Assert.Equal(0, await Pristine(s, 0));
        Assert.Equal(0, await Pristine(s, 2));

        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[0].Id, NewInk(s)));
        Assert.Null(await Pristine(s, 0));
        Assert.Equal(0, await Pristine(s, 1));
    }

    [Fact]
    public async Task SaveTwice_WithAnnotationOnOnePage_VerifiesAndKeepsIt()
    {
        var p = Path.Combine(_dir, "b.pdf");
        TestPdfFactory.CreateBlank(p, 3, 595, 842);
        await using var s = await DocumentSession.OpenAsync(_t, p, null);
        await s.AnnotationsReady;
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(s.Pages[1].Id, NewInk(s)));
        await Svc().SaveAsync(s, p);   // 1쪽(색인 1)은 센 쪽, 나머지는 건너뛴 쪽
        await Svc().SaveAsync(s, p);   // 두 번째 저장도 같은 방식으로 통과해야 한다(바뀐 쪽이 계속 건드린 쪽으로 남는다)

        var raw = RawAnnotCounts.TryRead(p, null);
        Assert.Equal(new[] { 0, 1, 0 }, raw);
    }

    [Fact]
    public void RawAnnotCounts_UnreadableFile_ReturnsNull()
    {
        var p = Path.Combine(_dir, "junk.pdf");
        File.WriteAllBytes(p, [1, 2, 3]);
        Assert.Null(RawAnnotCounts.TryRead(p, null));
    }
}
