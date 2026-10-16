using Pdf.Native;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.Document.Recovery;
using PdfEditor.TestFixtures;

namespace PdfEditor.Document.Tests.Annotations;

public class AnnotationLoadingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-aload-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public AnnotationLoadingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        AnnotationReader.TestFailure = null;
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    [Fact]
    public async Task Open_WithAnnotations_ReadyAttachesEditableOriginals()
    {
        AnnotatedPdfFactory.CreateMixed(P("m.pdf"));
        await using var s = await DocumentSession.OpenAsync(_t, P("m.pdf"), null);
        await s.AnnotationsReady;
        Assert.Equal(9, s.Annotations.Get(s.Pages[0].Id).Items.Length);
        Assert.True(s.OriginalAnnotationsEditable);
    }

    [Fact]
    public async Task Open_NoAnnotations_DoesNotOpenPdfSharp()
    {
        TestPdfFactory.CreateLabeled(P("l.pdf"), [(300, 400), (300, 400)]);
        int before = AnnotationReader.OpenCount;
        await using var s = await DocumentSession.OpenAsync(_t, P("l.pdf"), null);
        await s.AnnotationsReady;
        Assert.Equal(before, AnnotationReader.OpenCount);
        Assert.True(s.OriginalAnnotationsEditable);
        Assert.Empty(s.Annotations.Get(s.Pages[0].Id).Items);
    }

    [Fact]
    public async Task Open_UnreadableByPdfSharp_ReadyButOriginalsReadOnly()
    {
        AnnotatedPdfFactory.CreateMixed(P("m.pdf"));
        AnnotationReader.TestFailure = new InvalidOperationException("injected");
        await using var s = await DocumentSession.OpenAsync(_t, P("m.pdf"), null);
        await s.AnnotationsReady;   // 예외 없이 끝난다
        Assert.Empty(s.Annotations.Get(s.Pages[0].Id).Items);
        Assert.False(s.OriginalAnnotationsEditable);
    }

    [Fact]
    public async Task OpenRecovered_AttachesOriginals()
    {
        AnnotatedPdfFactory.CreateMixed(P("m.pdf"));
        var entry = new RecoveryEntry(Guid.NewGuid(), P("m.pdf"), null, null, "m.pdf", DateTimeOffset.UtcNow);
        await using var s = await DocumentSession.OpenRecoveredAsync(_t, entry, false, null);
        await s.AnnotationsReady;
        Assert.Equal(9, s.Annotations.Get(s.Pages[0].Id).Items.Length);
    }

    [Fact]
    public async Task CreateNew_ReadyImmediately()
    {
        await using var a = await DocumentSession.CreateNewAsync(_t);
        await using var b = await DocumentSession.CreateEmptyAsync(_t);
        Assert.True(a.AnnotationsReady.IsCompletedSuccessfully);
        Assert.True(b.AnnotationsReady.IsCompletedSuccessfully);
        Assert.True(a.OriginalAnnotationsEditable);
    }
}
