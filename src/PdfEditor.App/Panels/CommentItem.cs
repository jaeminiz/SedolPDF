using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Panels;

/// <summary>
/// 코멘트 목록의 한 줄. Id: 편집 가능한 주석(답글 포함, 바꾸기 묶음은 대표 StrikeOut), ReadOnlyIndex: 읽기 전용 원본의 /Annots 번호
/// (둘 중 하나만 있다). Depth: 답글이면 1. Editable이 false이면 회색으로 보이고 누르면 위치로 이동만 한다.
/// </summary>
public sealed record CommentItem(
    PageId Page, int PageNumber, AnnotId? Id, int? ReadOnlyIndex, string KindName, string? Author, string? Contents,
    DateTimeOffset? When, ReviewState State, int Depth, bool Editable, PdfRectF Rect);
