using PdfEditor.Document;
using PdfEditor.Document.Commands;

namespace PdfEditor.Pages;

public sealed class InsertBlankPageCommand(int index, float widthPt, float heightPt) : IUndoableCommand
{
    private PageId? _id;

    public void Execute(DocumentSession s)
    {
        if (index < 0 || index > s.Pages.Length) throw new ArgumentOutOfRangeException(nameof(index));
        s.Document.InsertBlankPage(index, widthPt, heightPt);   // 실패하면 아무것도 안 바뀜
        _id ??= s.NewPageIds(1)[0];
        s.InsertPageIds(index, [_id.Value]);
    }

    public void Undo(DocumentSession s)
    {
        int i = s.IndexOf(_id!.Value);
        s.Document.DeletePage(i);
        s.RemovePageIdAt(i);
    }
}
