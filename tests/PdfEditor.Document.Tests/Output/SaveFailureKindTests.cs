using Pdf.Native;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document.Content;
using PdfEditor.Document.Output;
using PdfEditor.TestFixtures;

namespace PdfEditor.Document.Tests.Output;

/// <summary>0.15.0: 저장 실패의 뜻(LookChanged / CannotRewrite). "그래도 저장"을 누르면 반드시 저장되고, 고쳐 쓸 수 없는 편집은 처음부터 원인을 알려 준다.</summary>
public sealed class SaveFailureKindTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-failkind-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public SaveFailureKindTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { _t.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    private string P(string n) => Path.Combine(_dir, n);

    private async Task<DocumentSession> Open(bool clipText)
    {
        string p = P(Guid.NewGuid().ToString("N") + ".pdf");
        ContentPdfFactory.CreateGradientWithTrickyText(p, clipText);
        return await DocumentSession.OpenAsync(_t, p, null);
    }

    private static OutputCopyBuilder WithPatcher(DocumentSession s, bool accept = false) => new(null, new PdfSharpAnnotationPatcher(() => []), s.Assets) { AcceptLookChanges = accept };

    private async Task<PageObjectEntry> ClipTextEntry(DocumentSession s)
        => (await _t.InvokeAsync(() => s.GetObjectIndex(s.Pages[0].Id).Entries.ToList())).Last(e => e.Type == PdfPageObjectType.Text);

    [Fact]
    public async Task NoPatcher_GradientMove_IsLookChanged_AndAcceptSaves()
    {
        await using var s = await Open(clipText: false);
        var id = s.Pages[0].Id;
        var all = await _t.InvokeAsync(() => s.GetObjectIndex(id).Entries.Select(e => e.Ref).ToList());
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [.. all.Select(r => (ContentEdit)new TransformEdit(s.Content.NewId(), r, PdfMatrix.Translate(3, -3)))]));
        var ex = await Assert.ThrowsAsync<ContentRegenerationException>(() => new OutputCopyBuilder(null, null, s.Assets).BuildAsync(s, P("o1.pdf")));
        Assert.Equal(RegenerationFailure.LookChanged, ex.Kind);
        var copy = await new OutputCopyBuilder(null, null, s.Assets) { AcceptLookChanges = true }.BuildAsync(s, P("o2.pdf"));
        Assert.True(File.Exists(P("o2.pdf")));
        Assert.NotEmpty(copy.LookWarnings!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrewritableDuplicate_IsCannotRewrite_FromTheStart_EvenWhenAccepted(bool accept)
    {
        await using var s = await Open(clipText: true);
        var id = s.Pages[0].Id;
        var clip = await ClipTextEntry(s);
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [new DuplicateObjectEdit(s.Content.NewId(), id, clip.Ref.OriginalIndex, PdfMatrix.Translate(0, -50))]));
        var ex = await Assert.ThrowsAsync<ContentRegenerationException>(() => WithPatcher(s, accept).BuildAsync(s, P("o.pdf")));
        Assert.Equal(RegenerationFailure.CannotRewrite, ex.Kind);
        Assert.Equal(1, ex.PageNumber);
        Assert.Contains("clip", ex.Reason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnrewritableMove_OnGradientPage_IsLookChanged_AndAcceptSaves()
    {
        await using var s = await Open(clipText: true);
        var id = s.Pages[0].Id;
        var clip = await ClipTextEntry(s);
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [new TransformEdit(s.Content.NewId(), clip.Ref, PdfMatrix.Translate(0, -50))]));
        var ex = await Assert.ThrowsAsync<ContentRegenerationException>(() => WithPatcher(s).BuildAsync(s, P("o1.pdf")));
        Assert.Equal(RegenerationFailure.LookChanged, ex.Kind);
        var copy = await WithPatcher(s, accept: true).BuildAsync(s, P("o2.pdf"));   // 0.14.0: 같은 사유로 다시 실패
        Assert.True(File.Exists(P("o2.pdf")));
        Assert.NotEmpty(copy.LookWarnings!);
    }
}
