using PdfEditor.App.Resources;
using PdfEditor.Document;
using PdfEditor.Optimize;

namespace PdfEditor.App.Documents;

/// <summary>목표를 못 맞췄을 때 사용자의 선택. 래스터화는 쪽을 이미지로 바꾸는 것(글자·링크·주석이 사라진다).</summary>
public enum NotAchievedChoice { KeepBestEffort, Rasterize, Monochrome, Cancel }

/// <summary>흑백 변환 화질 하나: 해상도와 그 결과 파일의 크기.</summary>
public sealed record MonoOption(int Dpi, long Bytes);

/// <summary>용량 줄이기의 대화상자들. 실제 구현은 Dialogs/AvaloniaReduceSizeDialogs, 시험에서는 가짜로 바꾼다.</summary>
public interface IReduceSizeDialogs
{
    /// <summary>목표 용량을 묻는다(기본 16 MB, 프리셋 8·16·20). 취소하면 null. currentBytes가 0이면 현재 용량을 모른다.</summary>
    Task<long?> AskTargetBytesAsync(long currentBytes);
    /// <summary>목표를 못 맞췄다고 알리고 고르게 한다. rasterAvailable가 false이면 "이미지로 변환" 선택지를 보이지 않는다. 창을 닫으면 Cancel.</summary>
    Task<NotAchievedChoice> AskNotAchievedAsync(string message, bool rasterAvailable);
    /// <summary>래스터화(monochrome이면 흑백 변환)의 부작용(글자·링크·주석, 색이 사라짐)을 경고하고 계속할지 묻는다.</summary>
    Task<bool> ConfirmRasterizeAsync(bool monochrome);
    /// <summary>흑백 변환의 화질(높은 해상도부터)을 크기와 함께 보여 주고 고르게 한다. 고른 번호, 취소하면 null.</summary>
    Task<int?> PickMonochromeLevelAsync(IReadOnlyList<MonoOption> options, long targetBytes);
}

/// <summary>
/// "용량 줄여 저장" 흐름(UI 스레드). 목표 용량 → 저장 위치 → 진행 창에서 줄이기 → 달성/못 맞춤 안내. 못 맞추면 그 상태로 저장·이미지로 변환·취소를 묻는다.
/// 원본은 건드리지 않고 항상 새 파일로 저장한다.
/// </summary>
public sealed class ReduceSizeWorkflow(IReduceSizeDialogs ui, IUserPrompts prompts, SizeReducer reducer)
{
    public async Task RunAsync(DocumentSession s)
    {
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
            var r = await ReduceWithProgressAsync(s, path, target, ReduceMode.Images).ConfigureAwait(true);
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
                bool rasterAvailable = r.Mode == ReduceMode.Images;   // 이미지 모드에서 못 맞췄을 때만 변환을 권한다
                var choice = await ui.AskNotAchievedAsync(message, rasterAvailable).ConfigureAwait(true);
                if (choice == NotAchievedChoice.KeepBestEffort)
                {
                    reducer.Commit(r, path);
                    pending = null;
                    await prompts.ShowInfoAsync(Done(r)).ConfigureAwait(true);
                    return;
                }
                if (choice == NotAchievedChoice.Monochrome && rasterAvailable && await ui.ConfirmRasterizeAsync(true).ConfigureAwait(true))
                {
                    reducer.Discard(r);
                    pending = null;
                    await RunMonochromeAsync(s, path, target).ConfigureAwait(true);
                    return;
                }
                if (choice == NotAchievedChoice.Rasterize && rasterAvailable && await ui.ConfirmRasterizeAsync(false).ConfigureAwait(true))
                {
                    reducer.Discard(r);
                    pending = null;
                    r = await ReduceWithProgressAsync(s, path, target, ReduceMode.Raster).ConfigureAwait(true);
                    continue;
                }
                return;   // 취소·확인 거절 (finally가 임시 결과를 버린다)
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await prompts.ShowErrorAsync(ErrorMessages.For(ex)).ConfigureAwait(true);
        }
        finally
        {
            if (pending is not null) reducer.Discard(pending);
        }
    }

    /// <summary>흑백 변환: 화질 3등급을 모두 만들어 크기를 보여 주고, 고른 것을 저장한다. 취소하면 아무것도 남기지 않는다.</summary>
    private async Task RunMonochromeAsync(DocumentSession s, string path, long target)
    {
        MonoVariants? variants = null;
        bool finished = await prompts.RunWithProgressAsync(Strings.Dlg_ReduceTitle, Strings.Msg_ReduceProgress, 100,
            async (progress, ct) => variants = await reducer.BuildMonochromeVariantsAsync(s, path, new Percent(progress), ct).ConfigureAwait(false)).ConfigureAwait(true);
        if (!finished || variants is null) return;
        try
        {
            var options = variants.Items.Select(v => new MonoOption(v.Dpi, v.Bytes)).ToList();
            if (await ui.PickMonochromeLevelAsync(options, target).ConfigureAwait(true) is not { } pick || pick < 0 || pick >= options.Count) return;
            reducer.CommitMonochrome(variants, pick, path);
            await prompts.ShowInfoAsync(string.Format(Strings.Msg_ReduceDone, ByteSize.Format(variants.OriginalBytes), ByteSize.Format(options[pick].Bytes))).ConfigureAwait(true);
            variants = null;
        }
        finally
        {
            if (variants is not null && variants.Items.Any(v => File.Exists(v.Path))) reducer.DiscardMonochrome(variants);
        }
    }

    private static string Done(ReduceResult r) => string.Format(Strings.Msg_ReduceDone, ByteSize.Format(r.OriginalBytes), ByteSize.Format(r.FinalBytes));

    /// <summary>진행 창(취소 가능)에서 줄인다. 사용자가 취소하면 null.</summary>
    private async Task<ReduceResult?> ReduceWithProgressAsync(DocumentSession s, string path, long target, ReduceMode mode)
    {
        ReduceResult? result = null;
        bool finished = await prompts.RunWithProgressAsync(Strings.Dlg_ReduceTitle, Strings.Msg_ReduceProgress, 100,
            async (progress, ct) => result = await reducer.ReduceAsync(s, path, target, mode, new Percent(progress), ct).ConfigureAwait(false)).ConfigureAwait(true);
        return finished ? result : null;
    }

    private sealed class Percent(IProgress<int> inner) : IProgress<ReduceProgress>
    {
        public void Report(ReduceProgress value) => inner.Report((int)Math.Clamp(value.Fraction * 100, 0, 100));
    }
}
