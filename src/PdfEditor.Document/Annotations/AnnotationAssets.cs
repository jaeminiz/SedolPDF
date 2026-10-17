namespace PdfEditor.Document.Annotations;

/// <summary>패치 때 임시 파일에 넣을 바이트(그림 스탬프 이미지, 첨부 파일). id로 찾는다.</summary>
public sealed class AnnotationAssets
{
    public static AnnotationAssets Empty { get; } = new();

    private readonly Dictionary<string, byte[]> _images = new();
    private readonly Dictionary<string, byte[]> _attachments = new();

    public void AddImage(string id, byte[] data) => _images[id] = data;
    public void AddAttachment(string id, byte[] data) => _attachments[id] = data;

    public byte[]? GetImage(string id) => _images.GetValueOrDefault(id);
    public byte[]? GetAttachment(string id) => _attachments.GetValueOrDefault(id);
}
