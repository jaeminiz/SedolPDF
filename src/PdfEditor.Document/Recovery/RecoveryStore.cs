using System.Text.Json;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.Document.Saving;

namespace PdfEditor.Document.Recovery;

/// <summary>
/// 자동 복구 스냅숏 저장소. 세션마다 <c>&lt;id&gt;.pdf</c>(현재 상태의 전체 사본)와 <c>&lt;id&gt;.json</c>(메타데이터)을 둔다.
/// 원본 파일은 건드리지 않는다. 쓰기는 임시 파일에 쓴 뒤 교체한다(PDF 먼저, JSON 나중: JSON이 있으면 PDF도 온전하다).
/// 주석 모델은 쓰기 직전 PDF에 반영(Flush)한 뒤 사본을 만든다 (별도 모델 스냅숏 없음, 계획 D5).
/// </summary>
public sealed class RecoveryStore(
    string folder,
    IAppearanceSource? appearance = null,
    IAnnotationPatcher? patcher = null,
    AnnotationAssets? assets = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Folder => folder;

    public static string DefaultFolder =>
        Environment.GetEnvironmentVariable("PDFEDITOR_RECOVERY_DIR") is { Length: > 0 } over
            ? over
            : DefaultFolderIn(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    /// <paramref name="localAppData"/>\SedolPDF\Recovery. 새 SedolPDF 폴더가 없고 옛 PdfEditor\Recovery가 있으면 그 파일을 한 번 복사한다(실패해도 무시).
    /// </summary>
    public static string DefaultFolderIn(string localAppData)
    {
        string newDir = Path.Combine(localAppData, "SedolPDF", "Recovery");
        try
        {
            string oldDir = Path.Combine(localAppData, "PdfEditor", "Recovery");
            string marker = Path.Combine(newDir, ".migrated");
            // 옛 폴더가 있고 "이전 완료" 표식이 없으면 (처음이거나 지난번에 일부 실패) 파일마다 따로 복사한다.
            // 이미 있는 파일은 덮어쓰지 않고, 옛 파일은 지우지 않는다. 하나라도 실패하면 표식을 쓰지 않아 다음 실행에서 다시 시도한다.
            if (Directory.Exists(oldDir) && !File.Exists(marker))
            {
                Directory.CreateDirectory(newDir);
                bool ok = true;
                foreach (var f in Directory.EnumerateFiles(oldDir))
                {
                    try
                    {
                        string dest = Path.Combine(newDir, Path.GetFileName(f));
                        if (!File.Exists(dest)) File.Copy(f, dest, overwrite: false);
                    }
                    catch (Exception) { ok = false; }
                }
                if (ok) File.WriteAllText(marker, "");
            }
        }
        catch (Exception) { /* 옮기지 못해도 빈 복구 폴더로 시작 */ }
        return newDir;
    }

    private string PdfPath(Guid id) => Path.Combine(folder, id.ToString("N") + ".pdf");
    private string JsonPath(Guid id) => Path.Combine(folder, id.ToString("N") + ".json");

    public async Task WriteAsync(DocumentSession s)
    {
        Directory.CreateDirectory(folder);
        string pdf = PdfPath(s.Id), pdfTmp = pdf + ".tmp";
        string json = JsonPath(s.Id), jsonTmp = json + ".tmp";
        try
        {
            // 복사본은 PDFium 전용 스레드에서 쓴다. 세션 상태는 바꾸지 않는다(더티 유지).
            // Flush가 실패하면 예외를 그대로 낸다(복구본을 쓰지 않음, 기존 복구본 유지). IsDirty는 바뀌지 않는다.
            var plan = await s.Thread.InvokeAsync(() =>
            {
                var p = s.FlushIfNeeded(appearance);
                s.Document.SaveCopy(pdfTmp);
                return p;
            }).ConfigureAwait(false);
            // 패치(PDFsharp)는 임시 파일에만 적용한다 (편집 중인 문서는 바꾸지 않음). 암호 문서는 건너뜀 (A2).
            if (!plan.IsEmpty && SavePolicy.CanPatch(s))
            {
                if (patcher is null)
                    throw new InvalidOperationException("Annotation patch plan needs an IAnnotationPatcher.");
                string? pw = s.Password;
                await Task.Run(() => patcher.Apply(pdfTmp, pw, plan, assets ?? AnnotationAssets.Empty)).ConfigureAwait(false);
            }
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
