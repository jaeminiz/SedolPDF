using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Content;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;

namespace PdfEditor.Pages.Tests;

/// <summary>0.9.0 Task 7: 다른 쪽에 붙인 개체의 원천 쪽은 지울 수 없다.</summary>
public sealed class DeletePagesCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-delcopy-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public DeletePagesCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<(DocumentSession S, PageId P1, PageId P2, long DupId)> OpenWithCopy()
    {
        string p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        ContentPdfFactory.CreateDocumentLike(p, pages: 2);
        var s = await DocumentSession.OpenAsync(_t, p, null);
        var p1 = s.Pages[0].Id; var p2 = s.Pages[1].Id;
        int idx = await _t.InvokeAsync(() => s.GetObjectIndex(p1).Entries.First(e => e.Type == PdfPageObjectType.Path).Ref.OriginalIndex);
        long did = s.Content.NewId();
        await s.Commands.ExecuteAsync(new ContentEditCommand(p2, [new DuplicateObjectEdit(did, p1, idx, PdfMatrix.Translate(5, 5))]));
        return (s, p1, p2, did);
    }

    [Fact]
    public async Task Delete_SourceOfLiveCopy_Throws_AndNothingChanges()
    {
        var (s, p1, _, _) = await OpenWithCopy();
        await using var _ = s;
        await Assert.ThrowsAsync<PageHasCopiesException>(() => s.Commands.ExecuteAsync(new DeletePagesCommand([p1])));
        Assert.Equal(2, s.Pages.Length);
        Assert.Equal(p1, s.Pages[0].Id);
    }

    [Fact]
    public async Task Delete_AfterCopyDeleted_Succeeds()
    {
        var (s, p1, p2, did) = await OpenWithCopy();
        await using var _ = s;
        await s.Commands.ExecuteAsync(new ContentEditCommand(p2, [new DeactivateEdit(s.Content.NewId(), ObjectRef.Inserted(did))]));
        await s.Commands.ExecuteAsync(new DeletePagesCommand([p1]));
        Assert.Single(s.Pages);
    }

    [Fact]
    public async Task Delete_SourceAndTargetTogether_Succeeds()
    {
        var (s, p1, p2, _) = await OpenWithCopy();
        await using var _ = s;
        // 두 쪽을 모두 지우면 문서가 비므로 쪽을 하나 더 둔다
        await s.Commands.ExecuteAsync(new InsertBlankPageCommand(2, 300, 300));
        await s.Commands.ExecuteAsync(new DeletePagesCommand([p1, p2]));
        Assert.Single(s.Pages);
    }
}
