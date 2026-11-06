using PdfEditor.App.Resources;
using PdfEditor.Document;
using PdfEditor.Optimize;

namespace PdfEditor.App.Documents;

/// <summary>
/// 목표를 못 맞췄을 때 사용자의 선택. ColorImages: 쪽을 컬러 이미지로 바꾼다(글자·링크·주석이 사라진다).
/// Layered: 도면용(색 유지) 변환 — 선·글자는 고해상도 흑백 마스크, 색은 저해상도 색층. Level은 <see cref="SizeLadder.Mono"/>의 번호(0 = 높음).
/// </summary>
public enum NotAchievedKind { KeepBestEffort, ColorImages, Layered, Cancel }

/// <param name="Kind">고른 방식.</param>
/// <param name="Level">Kind가 Layered일 때 고른 화질 번호(높음 0, 중간 1, 최저 2).</param>
public sealed record NotAchievedDecision(NotAchievedKind Kind, int Level = 0);

/// <summary>용량 줄이기의 대화상자들. 실제 구현은 Dialogs/AvaloniaReduceSizeDialogs, 시험에서는 가짜로 바꾼다.</summary>
public interface IReduceSizeDialogs
{
    /// <summary>목표 용량을 묻는다(기본 16 MB, 프리셋 8·16·20). 취소하면 null. currentBytes가 0이면 현재 용량을 모른다.</summary>
    Task<long?> AskTargetBytesAsync(long currentBytes);
    /// <summary>
    /// 목표를 못 맞췄다고 알리고 저장 방식을 고르게 한다. 그 상태로 저장할 때의 크기(bestEffortBytes)와, 변환할 때의 예상 용량(estimates)을 한 화면에 보여 준다.
    /// estimates가 null이면 변환 선택지 없이 "그 상태로 저장"과 취소만 있다. 창을 닫으면 Cancel.
    /// </summary>
    Task<NotAchievedDecision> AskNotAchievedAsync(string message, long bestEffortBytes, long targetBytes, ConversionEstimates? estimates);
}

/// <summary>
/// "용량 줄여 저장" 흐름(UI 스레드). 목표 용량 → 저장 위치 → 진행 창에서 줄이기 → 달성/못 맞춤 안내. 못 맞추면 변환했을 때의 예상 용량을 미리 재서
/// 그 상태로 저장·컬러 이미지로 변환·도면용(색 유지) 변환 3등급·취소 중에서 한 화면에서 고르게 한다. 원본은 건드리지 않고 항상 새 파일로 저장한다.
/// </summary>
public sealed class ReduceSizeWorkflow(IReduceSizeDialogs ui, IUserPrompts prompts, SizeReducer reducer, FileWorkflow? files = null)
{
    public async Task RunAsync(DocumentSession s)
    {
        // 줄이는 동안 이 세션의 탭을 다른 창으로 옮기지 못하게 표시한다(0.8.0 여러 창).
        using var _ = files?.MarkBusy(s);
        if (s.IsEncrypted) { await prompts.ShowInfoAsync(Strings.Msg_ReduceEncrypted).ConfigureAwait(true); return; }

        long current = s.FilePath is { } own && File.Exists(own) ? new FileInfo(own).Length : 0;
        if (await ui.AskTargetBytesAsync(current).ConfigureAwait(true) is not { } target) return;
        string suggested = string.Format(Strings.Doc_ReduceSuggestedName, Path.GetFileNameWithoutExtension(s.DisplayName));
        if (await prompts.PickSaveFileAsync(suggested).ConfigureAwait(true) is not { } picked) return;
        string path = Path.GetFullPath(picked);
        if (s.FilePath is { } src && string.Equals(Path.GetFullPath(src), path, StringComparison.OrdinalIgnoreCase))
        {
            await prompts.ShowInfoAsync(Strings.Msg_ReduceSameFile).ConfigureAwait(true);
            return;
        }

        ReduceResult? pending = null;
        try
        {
            var r = await ReduceWithProgressAsync(s, path, target, ReduceMode.Images, 0).ConfigureAwait(true);
            while (r is not null)
            {
                if (r.AlreadySmallEnough)
                {
                    await prompts.ShowInfoAsync(string.Format(Strings.Msg_ReduceAlready, ByteSize.Format(r.OriginalBytes))).ConfigureAwait(true);
                    return;
                }
                if (r.Achieved)
                {
                    await prompts.ShowInfoAsync(Done(r)).ConfigureAwait(true);
                    return;
                }

                pending = r;
                string message = string.Format(Strings.Msg_ReduceNotAchieved, ByteSize.Format(r.MinPossibleBytes), ByteSize.Format(target));
                if (r.MinPossibleBytes >= r.OriginalBytes * 0.9) message += " " + Strings.Msg_ReduceFewImages;

                // 이미지 모드에서 못 맞췄을 때만 변환을 권한다. 변환했을 때의 용량을 선택 창에 미리 보여 주려고 표본 쪽으로 먼저 잰다.
                ConversionEstimates? estimates = null;
                if (r.Mode == ReduceMode.Images)
                {
                    var estimated = await EstimateWithProgressAsync(s, path).ConfigureAwait(true);
                    if (estimated.Cancelled) return;   // 취소 (finally가 임시 결과를 버린다)
                    estimates = estimated.Estimates;
                }

                var decision = await ui.AskNotAchievedAsync(message, r.MinPossibleBytes, target, estimates).ConfigureAwait(true);
                if (decision.Kind == NotAchievedKind.KeepBestEffort)
                {
                    reducer.Commit(r, path);
                    pending = null;
                    await prompts.ShowInfoAsync(Done(r)).ConfigureAwait(true);
                    return;
                }
                if (estimates is not null && decision.Kind == NotAchievedKind.ColorImages)
                {
                    reducer.Discard(r);
                    pending = null;
                    r = await ReduceWithProgressAsync(s, path, target, ReduceMode.Raster, 0).ConfigureAwait(true);
                    continue;
                }
                if (estimates is not null && decision.Kind == NotAchievedKind.Layered && decision.Level >= 0 && decision.Level < SizeLadder.Mono.Count)
                {
                    reducer.Discard(r);
                    pending = null;
                    r = await ReduceWithProgressAsync(s, path, target, ReduceMode.Layered, decision.Level).ConfigureAwait(true);
                    continue;
                }
                return;   // 취소 (finally가 임시 결과를 버린다)
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorLog.Write("reduce", ex);
            await prompts.ShowErrorAsync(ErrorMessages.For(ex)).ConfigureAwait(true);
        }
        finally
        {
            if (pending is not null) reducer.Discard(pending);
        }
    }

    private static string Done(ReduceResult r) => string.Format(Strings.Msg_ReduceDone, ByteSize.Format(r.OriginalBytes), ByteSize.Format(r.FinalBytes));

    /// <summary>진행 창(취소 가능)에서 줄인다. 사용자가 취소하면 null.</summary>
    private async Task<ReduceResult?> ReduceWithProgressAsync(DocumentSession s, string path, long target, ReduceMode mode, int level)
    {
        ReduceResult? result = null;
        bool finished = await prompts.RunWithProgressAsync(Strings.Dlg_ReduceTitle, Strings.Msg_ReduceProgress, 100,
            async (progress, ct) => result = await reducer.ReduceAsync(s, path, target, mode, new Percent(progress), ct, level).ConfigureAwait(false)).ConfigureAwait(true);
        return finished ? result : null;
    }

    /// <summary>변환 예상 용량을 진행 창에서 잰다. 사용자가 취소하면 Cancelled, 재다가 실패하면(기록을 남기고) 예상 없이 계속한다.</summary>
    private async Task<(bool Cancelled, ConversionEstimates? Estimates)> EstimateWithProgressAsync(DocumentSession s, string path)
    {
        ConversionEstimates? estimates = null;
        bool finished = await prompts.RunWithProgressAsync(Strings.Dlg_ReduceTitle, Strings.Msg_ReduceProgress, 100, async (progress, ct) =>
        {
            try { estimates = await reducer.EstimateConversionsAsync(s, path, new Percent(progress), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { ErrorLog.Write("estimate", ex); }
        }).ConfigureAwait(true);
        return (!finished, estimates);
    }

    private sealed class Percent(IProgress<int> inner) : IProgress<ReduceProgress>
    {
        public void Report(ReduceProgress value) => inner.Report((int)Math.Clamp(value.Fraction * 100, 0, 100));
    }
}
