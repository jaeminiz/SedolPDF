namespace PdfEditor.Document.Annotations;

/// <summary>검토 상태(PDF /StateModel /Review). 화면 이름은 리소스 파일에서 가져오고, PDF 저장에는 규격 이름을 쓴다.</summary>
public enum ReviewState { None, Accepted, Rejected, Cancelled, Completed }

public static class ReviewStateExtensions
{
    /// <summary>PDF 규격 이름(/State 값).</summary>
    public static string ToPdfName(this ReviewState s) => s switch
    {
        ReviewState.None => "None",
        ReviewState.Accepted => "Accepted",
        ReviewState.Rejected => "Rejected",
        ReviewState.Cancelled => "Cancelled",
        ReviewState.Completed => "Completed",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static ReviewState? FromPdfName(string? name) => name switch
    {
        "None" => ReviewState.None,
        "Accepted" => ReviewState.Accepted,
        "Rejected" => ReviewState.Rejected,
        "Cancelled" => ReviewState.Cancelled,
        "Completed" => ReviewState.Completed,
        _ => null,
    };
}
