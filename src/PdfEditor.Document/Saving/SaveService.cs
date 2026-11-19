using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;

namespace PdfEditor.Document.Saving;

/// <summary>전체 저장 → 같은 폴더 임시 파일 → 후처리 → 다시 열어 검증 → 교체. 실패하면 원본은 그대로.</summary>
public sealed class SaveService(
    IReadOnlyList<ISavePostProcessor> postProcessors,
    IAppearanceSource? appearance = null,
    IAnnotationPatcher? patcher = null,
    AnnotationAssets? assets = null)
{
    /// <summary>마지막 저장에서 지운 원본 글 중 연산자째 지우지 못해 보이지 않는 글자로 남긴 수(M5 C12). 0보다 크면 옛 글이 검색에 나올 수 있다고 알린다.</summary>
    public int LastHiddenTextCount { get; private set; }

    /// <summary>마지막 저장에서 쪽 내용을 고친 쪽 중 이 문서에서 아직 알리지 않은 쪽의 번호(0부터). 없으면 빈 목록. 저장마다 처음에 비운다(0.10.0).</summary>
    public IReadOnlyList<int> LastRepairedPages { get; private set; } = [];

    private sealed class PageSet : HashSet<int>;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DocumentSession, PageSet> _reported = new();

    /// <summary>마지막 저장에서 화면 모양과 달라질 수 있다고 경고만 하고 저장한 쪽(acceptLookChanges일 때만 채워진다).</summary>
    public IReadOnlyList<Output.LookWarning> LastLookWarnings { get; private set; } = [];

    /// <summary>acceptLookChanges: 모양 검사에 걸려도 멈추지 않고 저장한다(사용자가 확인한 뒤). 덮어쓰는 저장이면 직전 파일을 `이름.bak.pdf`로 한 단계 남긴다.</summary>
    public async Task SaveAsync(DocumentSession s, string targetPath, CancellationToken ct = default, bool acceptLookChanges = false)
    {
        using var total = SaveTrace.Step("0.TOTAL");
        LastHiddenTextCount = 0;
        LastLookWarnings = [];
        LastRepairedPages = [];
        string dir = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
        string tmp = Path.Combine(dir, "~" + Path.GetFileName(targetPath) + ".tmp");
        var stage = SaveStage.Write;
        IReadOnlyList<int> repaired = [];
        IReadOnlyList<Output.LookWarning> lookWarnings = [];
        try
        {
            // ① 쓰기(주석 Flush + SaveCopy) ② 객체 편집 확정 + 패치(PDFsharp) — 모두 임시 파일에만 (OutputCopyBuilder, M4)
            var output = new Output.OutputCopyBuilder(appearance, patcher, assets) { AcceptLookChanges = acceptLookChanges };
            // 사본을 쓴 뒤(내용 확정·패치)의 실패는 PostProcess 단계로 알린다.
            var built = await output.BuildAsync(s, tmp, ct, onCopied: () => stage = SaveStage.PostProcess).ConfigureAwait(false);
            int[] expected = built.ExpectedAnnotCounts;
            int[]? rawBaseline = built.RawAnnotBaseline;
            LastHiddenTextCount = built.HiddenTextCount;
            repaired = built.RepairedPages ?? [];
            lookWarnings = built.LookWarnings ?? [];

            stage = SaveStage.PostProcess;
            foreach (var pp in postProcessors)
            {
                ct.ThrowIfCancellationRequested();
                if (pp.AppliesTo(s)) pp.Process(tmp, s.Password);
            }

            // ③ 다시 열어 검증
            stage = SaveStage.Verify;
            string? pw = s.Password;
            await s.Thread.InvokeAsync(() =>
            {
                using var _ = SaveTrace.Step("8.verify-reopen+counts");
                using var check = PdfDocument.Open(s.Thread, tmp, pw);
                if (check.PageCount != expected.Length)
                    throw new InvalidDataException($"쪽 수 불일치: 기대 {expected.Length}, 실제 {check.PageCount}");
                // 0.14.0: 주석을 건드리지 않은 쪽(rawBaseline[i] >= 0)은 PDFium으로 쪽을 불러 세지 않고, 사본 때의 /Annots 원시 길이와 최종 파일의 것을 비교한다(+ 가벼운 쪽 크기 확인).
                // 나머지 쪽(바뀐 쪽·암호 문서·원시 길이를 못 읽은 경우)은 예전처럼 PDFium으로 센다.
                int[]? rawFinal = rawBaseline is null ? null : RawAnnotCounts.TryRead(tmp, pw);
                if (rawFinal is not null && rawFinal.Length != expected.Length) rawFinal = null;
                for (int i = 0; i < expected.Length; i++)
                {
                    if (rawFinal is not null && rawBaseline![i] >= 0)
                    {
                        check.GetPageSizePt(i);
                        if (rawFinal[i] != rawBaseline[i])
                            throw new InvalidDataException($"{i + 1}쪽 주석 수 불일치: 기대 {rawBaseline[i]}, 실제 {rawFinal[i]}");
                        continue;
                    }
                    using var page = check.LoadPage(i);
                    int actual = page.AnnotationCount;
                    if (actual != expected[i])
                        throw new InvalidDataException($"{i + 1}쪽 주석 수 불일치: 기대 {expected[i]}, 실제 {actual}");
                }
            }, ct).ConfigureAwait(false);

            // ④ 교체
            stage = SaveStage.Replace;
            // 편집이 있으면 덮어쓰기 전의 파일을 글꼴 출처로 보관한다(저장 뒤에도 같은 쪽의 문단을 원래 글꼴로 다시 고칠 수 있게).
            using (SaveTrace.Step("9.retain-font-source")) if (s.Content.HasEdits) await Task.Run(s.RetainFontSource, ct).ConfigureAwait(false);
            using var replaceStep = SaveTrace.Step("10.replace-file");
            if (File.Exists(targetPath)) File.Replace(tmp, targetPath, lookWarnings.Count > 0 ? Path.ChangeExtension(targetPath, ".bak.pdf") : null);   // 모양이 달라질 수 있는 저장은 직전 파일을 한 단계 남긴다
            else File.Move(tmp, targetPath);
            LastLookWarnings = lookWarnings;
            // 교체까지 끝난 저장만 알린다. 다시 열기 전에는 메모리의 문서가 그대로라 저장할 때마다 같은 쪽을 또 고치게 되므로, 이미 알린 쪽은 다시 알리지 않는다.
            var seen = _reported.GetOrCreateValue(s);
            lock (seen) LastRepairedPages = [.. repaired.Where(seen.Add)];
        }
        catch (Exception e)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw new SaveException(stage, e);
        }
        // 저장한 파일의 해시를 새 "원본"으로 기록한다(복구 때 원본이 바뀌었는지 가리는 기준).
        byte[]? sha = null;
        try { using var _ = SaveTrace.Step("11.sha256"); sha = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(targetPath, ct).ConfigureAwait(false)); }
        catch (Exception) { /* 해시를 못 구해도 저장은 성공 */ }
        s.MarkSaved(targetPath, sha);
    }

}
