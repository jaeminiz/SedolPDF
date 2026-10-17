namespace PdfEditor.Document.Annotations;

public enum StampKind { Builtin, Text, Image }

/// <summary>
/// 스탬프 내용. Builtin: BuiltinName(PDF /Name, 예: Approved). Text: Text. Image: ImageId(AnnotationAssets의 키).
/// </summary>
public sealed record StampSpec(StampKind Kind, string BuiltinName, string? Text, string? ImageId);
