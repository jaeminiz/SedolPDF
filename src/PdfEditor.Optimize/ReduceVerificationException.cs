namespace PdfEditor.Optimize;

/// <summary>줄인 파일이 원본과 달라 보여(쪽 수가 다르거나 쪽이 비었거나 한 영역이 사라짐) 저장하지 않았다. 앱이 사용자에게 이유를 알린다.</summary>
public sealed class ReduceVerificationException() : InvalidOperationException("The reduced file does not look like the original.");
