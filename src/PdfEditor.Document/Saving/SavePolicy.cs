namespace PdfEditor.Document.Saving;

/// <summary>V6 결과(docs/validation/2026-10-m1-results.md)로 정한 저장 정책.</summary>
public static class SavePolicy
{
    /// <summary>PDFium FPDF_SaveAsCopy(플래그 0)가 암호화(사용자 암호·권한)를 유지하는가. V6 테스트로 확정.</summary>
    public const bool PreservesEncryption = true;

    public static bool CanOverwrite(DocumentSession s) => !s.IsEncrypted || PreservesEncryption;

    /// <summary>암호화가 유지되지 않으면 복구 파일에 암호 없는 사본을 남기지 않는다.</summary>
    public static bool CanWriteRecovery(DocumentSession s) => !s.IsEncrypted || PreservesEncryption;

    /// <summary>PDFsharp 패치 단계를 쓸 수 있는가. 암호 PDF(사용자·권한 암호)는 건너뛴다(A2).</summary>
    public static bool CanPatch(DocumentSession s) => !s.IsEncrypted;
}
