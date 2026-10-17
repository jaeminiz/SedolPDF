using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Exchange.Xfdf;

namespace PdfEditor.App.Documents;

/// <summary>가져오기 결과. Details: 건너뜀 이유("요소: 이유")와 참고 사항을 합친 목록.</summary>
public sealed record XfdfImportOutcome(int Imported, int SkippedCount, IReadOnlyList<string> Details);

/// <summary>
/// XFDF 파일을 활성 문서에 가져온다. 파일 읽기와 계획 세우기(스냅숏만 읽음)는 배경 스레드에서 하고,
/// 명령은 탭의 주석 명령 길(실행 취소 한 번)로 실행한다.
/// 파일 전체가 XFDF가 아니거나 너무 크면 <see cref="XfdfFormatException"/>을 던진다(앱이 알림으로 바꿈).
/// </summary>
public static class XfdfImporter
{
    public static async Task<XfdfImportOutcome> ImportAsync(DocumentTabViewModel tab, AnnotationAssets assets, string path)
    {
        var s = tab.Session ?? throw new InvalidOperationException("No document is open.");
        try
        {
            await s.AnnotationsReady;
            // 주석을 더할 권한이 없으면 읽기만 하고 모두 건너뛴다(명령을 실행하지 않는다).
            bool canAnnotate = tab.CanAnnotate;
            var (plan, read) = await Task.Run(() =>
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var rd = XfdfReader.Read(fs, s.Annotations.NewId, assets);
                return (canAnnotate ? XfdfImport.Plan(s, rd) : null, rd);
            });
            if (plan is null)
                return new XfdfImportOutcome(0, read.Annotations.Count + read.Skipped.Count,
                    [.. read.Skipped, .. read.Annotations.Select(_ => "annotation: this document does not allow adding annotations")]);
            // 읽는 동안 탭이 닫혔거나 바뀌었으면 아무것도 하지 않는다.
            if (!ReferenceEquals(tab.Session, s)) return new XfdfImportOutcome(0, 0, []);
            if (plan.Command is not null) await tab.RunAnnotationCommandAsync(plan.Command);
            return new XfdfImportOutcome(plan.Imported, plan.Skipped.Count, [.. plan.Skipped, .. plan.Notes]);
        }
        catch (ObjectDisposedException)
        {
            return new XfdfImportOutcome(0, 0, []);   // 읽는 동안 문서가 닫혔다
        }
    }
}
