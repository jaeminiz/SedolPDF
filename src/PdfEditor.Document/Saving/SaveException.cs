namespace PdfEditor.Document.Saving;

public enum SaveStage { Write, PostProcess, Verify, Replace }

/// <summary>저장 파이프라인의 한 단계가 실패했다. 원본 파일은 그대로이고 임시 파일은 지우려고 시도했다.</summary>
public sealed class SaveException(SaveStage stage, Exception inner)
    : Exception($"저장 실패 ({stage}): {inner.Message}", inner)
{
    public SaveStage Stage { get; } = stage;
}
