namespace PdfEditor.Document;

/// <summary>세션 안에서 페이지를 가리키는 번호. 페이지가 옮겨져도 바뀌지 않는다 (인덱스와 다름).</summary>
public readonly record struct PageId(long Value);
