using System.Text.Json;

namespace PdfEditor.Document.Recovery;

/// <summary>
/// 자동 복구 스냅숏 저장소. 세션마다 <c>&lt;id&gt;.pdf</c>(현재 상태의 전체 사본)와 <c>&lt;id&gt;.json</c>(메타데이터)을 둔다.
/// 원본 파일은 건드리지 않는다. 쓰기는 임시 파일에 쓴 뒤 교체한다(PDF 먼저, JSON 나중: JSON이 있으면 PDF도 온전하다).
/// M2부터는 주석 모델 스냅숏도 함께 기록해야 한다 (PDF에는 아직 AP로 굳지 않은 편집 상태가 있을 수 있음).
/// </summary>
public sealed class RecoveryStore(string folder)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Folder => folder;

    public static string DefaultFolder =>
        Environment.GetEnvironmentVariable("PDFEDITOR_RECOVERY_DIR") is { Length: > 0 } over
            ? over
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfEditor", "Recovery");

    private string PdfPath(Guid id) => Path.Combine(folder, id.ToString("N") + ".pdf");
    private string JsonPath(Guid id) => Path.Combine(folder, id.ToString("N") + ".json");

    public async Task WriteAsync(DocumentSession s)
    {
        Directory.CreateDirectory(folder);
        string pdf = PdfPath(s.Id), pdfTmp = pdf + ".tmp";
        string json = JsonPath(s.Id), jsonTmp = json + ".tmp";
        // 복사본은 PDFium 전용 스레드에서 쓴다. 세션 상태는 바꾸지 않는다(더티 유지).
        await s.Thread.InvokeAsync(() => s.Document.SaveCopy(pdfTmp)).ConfigureAwait(false);
        try
        {
            File.Move(pdfTmp, pdf, overwrite: true);
            var entry = new RecoveryEntry(s.Id, pdf, s.FilePath,
                s.OriginalSha256 is { } h ? Convert.ToHexString(h) : null, s.DisplayName, DateTimeOffset.Now);
            await File.WriteAllTextAsync(jsonTmp, JsonSerializer.Serialize(entry, Json), new System.Text.UTF8Encoding(false))
                .ConfigureAwait(false);
            File.Move(jsonTmp, json, overwrite: true);
        }
        finally
        {
            TryDelete(pdfTmp);
            TryDelete(jsonTmp);
        }
    }

    public void Delete(Guid sessionId)
    {
        // JSON을 먼저 지워서 PDF만 남는 일이 없게 한다(반대 순서면 JSON만 남을 수 있음)
        TryDelete(JsonPath(sessionId));
        TryDelete(PdfPath(sessionId));
        TryDelete(PdfPath(sessionId) + ".tmp");
        TryDelete(JsonPath(sessionId) + ".tmp");
    }

    /// <summary>JSON이 깨졌거나 PDF가 없는 항목은 건너뛴다(예외 없음). 오래된 순.</summary>
    public IReadOnlyList<RecoveryEntry> List()
    {
        var result = new List<RecoveryEntry>();
        try
        {
            if (!Directory.Exists(folder)) return result;
            foreach (string f in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    var e = JsonSerializer.Deserialize<RecoveryEntry>(File.ReadAllText(f));
                    if (e is null || e.SessionId == Guid.Empty) continue;
                    // 경로는 폴더에서 다시 계산한다(폴더를 옮겨도 동작)
                    string pdf = PdfPath(e.SessionId);
                    if (!File.Exists(pdf)) continue;
                    if (!string.Equals(Path.GetFileNameWithoutExtension(f), e.SessionId.ToString("N"), StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(e with { RecoveryPdfPath = pdf });
                }
                catch (Exception) { /* 깨진 항목은 건너뜀 */ }
            }
        }
        catch (Exception) { /* 폴더 열거 실패: 빈 목록 */ }
        return result.OrderBy(e => e.SavedAt).ToList();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }
}
