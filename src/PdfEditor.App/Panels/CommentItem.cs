using CommunityToolkit.Mvvm.ComponentModel;
using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Panels;

/// <summary>줄 하나의 화면 상태(답글 입력칸이 열려 있는지, 쓰던 글). 목록을 다시 만들면 새로 시작한다.</summary>
public sealed partial class CommentRowUi : ObservableObject
{
    [ObservableProperty] private bool _isReplying;
    [ObservableProperty] private string _draft = "";
}

/// <summary>
/// 코멘트 목록의 한 줄. Id: 편집 가능한 주석(답글 포함, 바꾸기 묶음은 대표 StrikeOut), ReadOnlyIndex: 읽기 전용 원본의 /Annots 번호
/// (둘 중 하나만 있다). Depth: 답글이면 1. Editable이 false이면 회색으로 보이고 누르면 위치로 이동만 한다.
/// </summary>
public sealed record CommentItem(
    PageId Page, int PageNumber, AnnotId? Id, int? ReadOnlyIndex, string KindName, string? Author, string? Contents,
    DateTimeOffset? When, ReviewState State, int Depth, bool Editable, PdfRectF Rect, string IconKey = "generic")
{
    /// <summary>화면 전용 상태 (줄 모양 계산에는 쓰지 않는다).</summary>
    public CommentRowUi Ui { get; } = new();

    public bool IsReply => Depth > 0;
    public bool IsReadOnly => !Editable;
    public bool HasState => State != ReviewState.None;
    public string StateText => CommentsViewModel.StateName(State);
    public string? TimeText => When?.LocalDateTime.ToString("g");
    public string PageText => PageNumber.ToString();
    public Avalonia.Thickness Indent => new(Depth * 16, 0, 0, 0);
    public double RowOpacity => Editable ? 1.0 : 0.6;
    public string AuthorLine => string.Join(" · ", new[] { Author, TimeText }.Where(s => !string.IsNullOrWhiteSpace(s)));
}
