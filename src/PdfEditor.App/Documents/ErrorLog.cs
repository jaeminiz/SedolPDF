namespace PdfEditor.App.Documents;

/// <summary>
/// 예기치 않은 오류의 원인을 남기는 작은 기록 파일(%LOCALAPPDATA%\SedolPDF\error.log). 화면에는 일반 문구만 보이므로, 한 번 나고 다시 안 나는 오류도 나중에 볼 수 있게 한다.
/// 기록하다 실패해도(디스크 가득 참·권한 등) 아무것도 던지지 않는다.
/// </summary>
public static class ErrorLog
{
    private const long MaxBytes = 512 * 1024;

    /// <summary>기록 파일 경로. 시험에서 바꾼다.</summary>
    public static string Path { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SedolPDF", "error.log");

    public static void Write(string area, Exception ex)
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes) File.Delete(Path);   // 너무 커지면 처음부터
            File.AppendAllText(Path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {area}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception) { }
    }
}
