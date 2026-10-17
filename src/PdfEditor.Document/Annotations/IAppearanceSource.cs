using Pdf.Native;
using PdfEditor.Document.Annotations.Patching;

namespace PdfEditor.Document.Annotations;

/// <summary>패치 길 주석의 AP: 내용 스트림, 경계(= 주석 Rect), 쓴 글자(글꼴 서브셋용).</summary>
public sealed record PatchAppearance(string Content, PdfRectF BBox, FontUse Font);

/// <summary>주석의 AP 내용 스트림을 만드는 쪽(구현은 Appearance 모듈).</summary>
public interface IAppearanceSource
{
    string BuildContent(Annotation a, int pageRotation);

    /// <summary>패치 길(글꼴 자원이 필요한 FreeText 등)용. 내용 스트림과 글꼴 서브셋에 쓸 글자 정보를 함께 돌려준다.</summary>
    PatchAppearance BuildPatchAppearance(Annotation a, int pageRotation);
}
