namespace PdfEditor.Document.Annotations;

/// <summary>세션 안에서 주석을 가리키는 번호. 목록 순서가 바뀌어도 변하지 않는다.</summary>
public readonly record struct AnnotId(long Value);
