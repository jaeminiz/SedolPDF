using Pdf.Native;
using PdfEditor.Document.Content;
using PdfEditor.Document.Output;
using PdfEditor.TestFixtures;

namespace PdfEditor.Document.Tests.Output;

/// <summary>썸네일 "페이지 추출"(2026-10-20 사용자 요청).</summary>
public sealed class PageExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-extract-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public PageExtractorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task Extract_SelectedPages_InOrder_WithEdits_OriginalUntouched()
    {
        string src = Path.Combine(_dir, "src.pdf");
        ContentPdfFactory.CreateDocumentLike(src, pages: 4);
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        // 3쪽(인덱스 2)의 이미지를 지운 편집이 추출본에도 반영돼야 한다
        var id = s.Pages[2].Id;
        int img = await _t.InvokeAsync(() => s.GetObjectIndex(id).Entries.First(e => e.Type == PdfPageObjectType.Image).Ref.OriginalIndex);
        await s.Commands.ExecuteAsync(new ContentEditCommand(id, [new DeactivateEdit(s.Content.NewId(), ObjectRef.Original(img))]));
        string target = Path.Combine(_dir, "out.pdf");
        await PageExtractor.ExtractAsync(s, new OutputCopyBuilder(null, null, null), [1, 2], target);

        var (count, texts, images) = await _t.InvokeAsync(() =>
        {
            using var d = PdfDocument.Open(_t, target);
            var t = new List<string>();
            var im = new List<int>();
            for (int i = 0; i < d.PageCount; i++)
            {
                using var p = d.LoadPage(i);
                using var tp = p.LoadText();
                t.Add(tp.GetText(0, tp.CharCount));
                im.Add(Enumerable.Range(0, p.ObjectCount).Count(k => p.GetObject(k).Type == PdfPageObjectType.Image));
            }
            return (d.PageCount, t, im);
        });
        Assert.Equal(2, count);
        Assert.Contains("p2", texts[0]);
        Assert.Contains("p3", texts[1]);
        Assert.Equal([1, 0], images);   // 3쪽의 지운 이미지는 없다
        Assert.Equal(4, s.Pages.Length);   // 편집 중인 문서는 그대로
        Assert.Empty(Directory.GetFiles(_dir, "~*.tmp"));
    }
}
