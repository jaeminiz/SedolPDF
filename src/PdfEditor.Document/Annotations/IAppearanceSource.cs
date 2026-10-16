namespace PdfEditor.Document.Annotations;

/// <summary>주석의 AP 내용 스트림을 만드는 쪽(구현은 Appearance 모듈).</summary>
public interface IAppearanceSource
{
    string BuildContent(Annotation a, int pageRotation);
}
