using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Exchange.Xfdf;

namespace PdfEditor.App.Documents;

/// <summary>가져오기 결과. Details: 건너뜀 이유("요소: 이유")와 참고 사항을 합친 목록.</summary>
public sealed record XfdfImportOutcome(int Imported, int SkippedCount, IReadOnlyList<string> Details);

/// <summary>
/// XFDF 파일을 활성 문서에 가져온다. 파일 읽기·연결(입출력과 무거운 일)은 배경 스레드에서 하고,
/// 기존 주석과 맞추는 계획 세우기는 UI 스레드에서 명령을 실행하기 직전에 한다(그 사이 사용자가 고친 주석을 오래된 모습으로 바꾸지 않도록).
/// 파일 전체가 XFDF가 아니거나 너무 크면 <see cref="XfdfFormatException"/>을 던진다(앱이 알림으로 바꿈).
/// </summary>
public static class XfdfImporter
{
    public static async Task<XfdfImportOutcome> ImportAsync(DocumentTabViewModel tab, AnnotationAssets assets, string path)
    {
        var s = tab.Session ?? throw new InvalidOperationException("No document is open.");
        XfdfReadResult? read = null;
        try
        {
            await s.AnnotationsReady;
            read = await Task.Run(() =>
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return XfdfReader.Read(fs, s.Annotations.NewId, assets);
            });
            // 주석을 더할 권한이 없으면 모두 건너뛴다(명령을 실행하지 않는다).
            if (!tab.CanAnnotate)
                return new XfdfImportOutcome(0, read.Annotations.Count + read.Skipped.Count,
                    [.. read.Skipped, .. read.Annotations.Select(_ => "annotation: this document does not allow adding annotations")]);
            // 읽는 동안 탭이 닫혔거나 바뀌었으면 아무것도 하지 않는다.
            if (!ReferenceEquals(tab.Session, s)) return new XfdfImportOutcome(0, 0, []);

            // 계획 직후 다른 변경이 끼어들어 명령이 오래된 상태라고 거절하면 한 번 더 계획한다(CompositeCommand는 스스로 되돌린다).
            for (int attempt = 0; ; attempt++)
            {
                var plan = XfdfImport.Plan(s, read);
                try
                {
                    if (plan.Command is not null) await tab.RunAnnotationCommandAsync(plan.Command);
                    return new XfdfImportOutcome(plan.Imported, plan.Skipped.Count, [.. plan.Skipped, .. plan.Notes]);
                }
                catch (InvalidOperationException) when (attempt == 0) { }
            }
        }
        catch (ObjectDisposedException)
        {
            return new XfdfImportOutcome(0, 0, []);   // 읽는 동안 문서가 닫혔다
        }
        finally
        {
            read?.ReleasePending();
        }
    }
}
