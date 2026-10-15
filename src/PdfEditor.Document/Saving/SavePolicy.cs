namespace PdfEditor.Document.Saving;

/// <summary>V6 결과(docs/validation/2026-10-m1-results.md)로 정한 저장 정책.</summary>
public static class SavePolicy
{
    /// <summary>PDFium FPDF_SaveAsCopy(플래그 0)가 암호화(사용자 암호·권한)를 유지하는가. V6 테스트로 확정.</summary>
    public const bool PreservesEncryption = true;

    public static bool CanOverwrite(DocumentSession s) => !s.IsEncrypted || PreservesEncryption;

    /// <summary>암호화가 유지되지 않으면 복구 파일에 암호 없는 사본을 남기지 않는다.</summary>
    public static bool CanWriteRecovery(DocumentSession s) => !s.IsEncrypted || PreservesEncryption;
}
