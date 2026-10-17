using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Exchange.Xfdf;

namespace PdfEditor.App.Documents;

/// <summary>가져오기 결과. Details: 건너뜀 이유("요소: 이유")와 참고 사항을 합친 목록.</summary>
public sealed record XfdfImportOutcome(int Imported, int SkippedCount, IReadOnlyList<string> Details);

/// <summary>
/// XFDF 파일을 활성 문서에 가져온다. 파일은 배경 스레드에서 읽고, 명령은 탭의 주석 명령 길(실행 취소 한 번)로 실행한다.
/// 파일 전체가 XFDF가 아니거나 너무 크면 <see cref="XfdfFormatException"/>을 던진다(앱이 알림으로 바꿈).
/// </summary>
public static class XfdfImporter
{
    public static async Task<XfdfImportOutcome> ImportAsync(DocumentTabViewModel tab, AnnotationAssets assets, string path)
    {
        var s = tab.Session ?? throw new InvalidOperationException("No document is open.");
        await s.AnnotationsReady;
        var read = await Task.Run(() =>
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return XfdfReader.Read(fs, s.Annotations.NewId, assets);
        });
        // 주석을 더할 권한이 없으면 모두 건너뛴다(명령을 실행하지 않는다).
        if (!tab.CanAnnotate)
            return new XfdfImportOutcome(0, read.Annotations.Count + read.Skipped.Count,
                [.. read.Skipped, .. read.Annotations.Select(_ => "annotation: this document does not allow adding annotations")]);
        var plan = XfdfImport.Plan(s, read);
        if (plan.Command is not null) await tab.RunAnnotationCommandAsync(plan.Command);
        return new XfdfImportOutcome(plan.Imported, plan.Skipped.Count, [.. plan.Skipped, .. plan.Notes]);
    }
}
