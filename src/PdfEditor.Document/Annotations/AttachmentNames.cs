namespace PdfEditor.Document.Annotations;

/// <summary>첨부 파일 이름 정리. 파일에서 읽은 이름과 파일에 쓰는 이름 모두 경로·제어 문자를 없애 "다른 이름으로 저장" 때 엉뚱한 곳에 쓰이지 않게 한다.</summary>
public static class AttachmentNames
{
    public const string Fallback = "attachment";

    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Fallback;
        string n = new([.. name.Where(c => !char.IsControl(c))]);
        n = Path.GetFileName(n.Replace('\\', '/'));
        // 경로 이동 조각과 Windows에서 쓸 수 없는 문자를 없앤다.
        foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c.ToString(), "");
        while (n.Contains("..")) n = n.Replace("..", ".");
        n = n.Trim(' ', '.');
        return n.Length == 0 ? Fallback : n;
    }
}
