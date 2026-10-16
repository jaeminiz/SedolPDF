namespace PdfEditor.Document.Recovery;

/// <summary>복구 폴더의 항목 하나. OriginalSha256Hex는 기록 시점에 디스크에 있던 원본의 SHA-256(16진수).</summary>
public sealed record RecoveryEntry(Guid SessionId, string RecoveryPdfPath, string? OriginalPath, string? OriginalSha256Hex,
    string DisplayName, DateTimeOffset SavedAt);
