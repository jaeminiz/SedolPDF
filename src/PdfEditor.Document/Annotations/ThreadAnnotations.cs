using Pdf.Native;

namespace PdfEditor.Document.Annotations;

/// <summary>
/// 새 답글·검토 상태 주석을 만든다(계획 B1). 둘 다 Text + /IRT이며 그리는 것이 없다(빈 AP).
/// 화면 문구(상태 /Contents)는 앱이 리소스 파일에서 가져온 틀을 넘긴다 — 여기에는 화면 문구를 두지 않는다.
/// </summary>
public static class ThreadAnnotations
{
    public const int ReplyFlags = PdfAnnotFlags.Print | PdfAnnotFlags.NoZoom | PdfAnnotFlags.NoRotate;
    public const int StateFlags = ReplyFlags | PdfAnnotFlags.Hidden;

    /// <summary>parent 아래 새 답글. Rect는 부모의 Rect(저장 때 부모의 현재 Rect를 다시 쓴다).</summary>
    public static Annotation NewReply(AnnotationStore store, Annotation parent, string text, string? author, DateTimeOffset now)
        => new()
        {
            Id = store.NewId(), Kind = AnnotKind.Text, Rect = parent.Rect, InReplyTo = parent.Id, ReplyKind = ReplyType.Reply,
            Contents = text, Author = author, CreatedAt = now, ModifiedAt = now, Flags = ReplyFlags,
        };

    /// <summary>
    /// parent에 상태를 단다. contentsTemplate은 리소스의 "{0} 님이 상태를 {1}로 설정" 같은 틀({0} = 작성자, {1} = 화면에 보이는 상태 이름).
    /// </summary>
    public static Annotation NewState(AnnotationStore store, Annotation parent, ReviewState state, string? author, DateTimeOffset now,
        string contentsTemplate, string stateDisplayName)
        => new()
        {
            Id = store.NewId(), Kind = AnnotKind.Text, Rect = parent.Rect, InReplyTo = parent.Id, ReplyKind = ReplyType.Reply,
            State = state, Contents = string.Format(contentsTemplate, author ?? "", stateDisplayName), Author = author,
            CreatedAt = now, ModifiedAt = now, Flags = StateFlags,
        };
}
