using PdfEditor.Document;
using PdfEditor.Document.Output;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Optimize;

public enum ReduceMode { Images, Raster }

/// <param name="Stage">"copy", "clean", "images", "raster", "verify" 중 하나.</param>
/// <param name="Fraction">전체 진행 비율(0~1).</param>
public sealed record ReduceProgress(string Stage, double Fraction);

/// <summary>
/// 용량 줄이기 결과. AlreadySmallEnough: 이미 목표 이하라 아무것도 쓰지 않았다. Achieved: 목표 이하로 만들어 targetPath에 썼다.
/// Achieved가 false이면 가장 세게 줄인 결과가 PendingPath(임시 파일)에 있고 MinPossibleBytes가 그 크기다 — Commit으로 그 상태를 저장하거나 Discard로 버린다.
/// </summary>
public sealed record ReduceResult(bool AlreadySmallEnough, bool Achieved, long OriginalBytes, long FinalBytes,
    long MinPossibleBytes, ReduceMode Mode, int Level, string? PendingPath);

/// <summary>흑백 변환의 한 등급 결과. Dpi: 변환 해상도, Bytes: 그 파일의 크기, Path: 임시 파일.</summary>
public sealed record MonoVariant(int Dpi, long Bytes, string Path);

/// <summary>흑백 변환의 등급별 결과(높은 해상도부터)와 원본 크기.</summary>
public sealed record MonoVariants(IReadOnlyList<MonoVariant> Items, long OriginalBytes);

/// <summary>
/// 용량 줄여 저장(스펙 2026-11-05). 편집 중인 문서는 <see cref="OutputCopyBuilder"/>로 임시 사본을 만들어(편집 세션은 그대로) 그 사본을 줄인다.
/// Images 모드: 무손실 정리 → 이미지 재압축 단계를 실제 저장 크기로 이진 탐색. Raster 모드: 쪽을 JPEG 이미지로 바꾼다(글자·링크·주석 사라짐).
/// 임시 파일은 targetPath와 같은 폴더에 *.tmp로 만들고 모든 경로(예외·취소 포함)에서 정리한다. 암호 문서는 지원하지 않는다.
/// </summary>
public sealed class SizeReducer(OutputCopyBuilder builder)
{
    public async Task<ReduceResult> ReduceAsync(DocumentSession s, string targetPath, long targetBytes, ReduceMode mode,
        IProgress<ReduceProgress>? progress, CancellationToken ct)
    {
        if (s.IsEncrypted) throw new NotSupportedException("Encrypted documents are not supported.");
        targetBytes += targetBytes / 20;   // 목표의 5%까지는 허용한다(16MB 목표면 16.8MB). 첨부 한도에는 여유가 있고, 허용하지 않으면 글자를 뭉개야만 맞는 파일이 생긴다.
        string target = Path.GetFullPath(targetPath);
        if (s.FilePath is { } fp && string.Equals(Path.GetFullPath(fp), target, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The target must differ from the source file.", nameof(targetPath));

        string copy = target + ".copy.tmp";
        string pending = target + ".reduce.tmp";
        var levelFiles = new List<string>();
        bool keepPending = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ReduceProgress("copy", 0));
            await builder.BuildAsync(s, copy, ct).ConfigureAwait(false);
            long copyBytes = new FileInfo(copy).Length;
            bool hasFile = s.FilePath is { } sp && File.Exists(sp);
            long fileBytes = hasFile ? new FileInfo(s.FilePath!).Length : copyBytes;
            long original = Math.Max(fileBytes, copyBytes);
            if (copyBytes <= targetBytes)
            {
                // 디스크의 파일도 목표 이하면 줄일 필요가 없다. 파일은 크지만(증분 저장으로 쌓인 옛 객체 등) 다시 쓴 사본이 목표 안이면 그 사본이 곧 결과다.
                if (fileBytes <= targetBytes)
                    return new ReduceResult(true, true, original, original, original, mode, 0, null);
                File.Move(copy, target, overwrite: true);
                return new ReduceResult(false, true, original, copyBytes, copyBytes, mode, -1, null);
            }

            var result = await Task.Run(() => mode == ReduceMode.Images
                ? RunImages(s, copy, target, pending, targetBytes, original, progress, ct)
                : RunRaster(s, copy, target, pending, levelFiles, targetBytes, original, progress, ct), ct).ConfigureAwait(false);
            keepPending = result.PendingPath is not null;
            return result;
        }
        finally
        {
            TryDelete(copy);
            foreach (var f in levelFiles) TryDelete(f);
            if (!keepPending) TryDelete(pending);
        }
    }

    /// <summary>목표 이하를 못 맞춘 결과(PendingPath)를 targetPath로 옮긴다("그 상태로 저장").</summary>
    public void Commit(ReduceResult r, string targetPath)
    {
        if (r.PendingPath is null) throw new InvalidOperationException("There is no pending result.");
        File.Move(r.PendingPath, Path.GetFullPath(targetPath), overwrite: true);
    }

    /// <summary>목표 이하를 못 맞춘 결과(PendingPath)를 버린다.</summary>
    public void Discard(ReduceResult r)
    {
        if (r.PendingPath is { } p) TryDelete(p);
    }

    private ReduceResult RunImages(DocumentSession s, string copy, string target, string pending, long targetBytes, long original,
        IProgress<ReduceProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new ReduceProgress("clean", 0.1));
        byte[] cleaned = LosslessCleaner.Clean(File.ReadAllBytes(copy));
        ct.ThrowIfCancellationRequested();

        int n = SizeLadder.Image.Count;
        var outputs = new Dictionary<int, byte[]>();
        var bilevelCache = new BilevelCache();
        long Measure(int i)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ReduceProgress("images", 0.2 + 0.6 * i / n));
            using var input = new MemoryStream(cleaned, writable: false);
            using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
            ImageRecompressor.Recompress(doc, SizeLadder.Image[i], ct, s.Thread, bilevelCache);
            doc.Options.CompressContentStreams = true;
            using var o = new MemoryStream();
            doc.Save(o, closeStream: false);
            outputs[i] = o.ToArray();
            return outputs[i].Length;
        }

        var search = SizeLadder.FirstFitting(n, Measure, targetBytes);
        int level = search.Level ?? n - 1;
        File.WriteAllBytes(pending, outputs[level]);
        return Finish(s, copy, target, pending, search, level, ReduceMode.Images, original, progress);
    }

    private ReduceResult RunRaster(DocumentSession s, string copy, string target, string pending, List<string> levelFiles, long targetBytes, long original,
        IProgress<ReduceProgress>? progress, CancellationToken ct)
    {
        int n = SizeLadder.Raster.Count;
        string PathOf(int i) => target + $".r{i}.tmp";
        long Measure(int i)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ReduceProgress("raster", 0.1 + 0.7 * i / n));
            string p = PathOf(i);
            levelFiles.Add(p);
            Rasterizer.Build(s.Thread, copy, null, p, SizeLadder.Raster[i], ct);
            return new FileInfo(p).Length;
        }

        var search = SizeLadder.FirstFitting(n, Measure, targetBytes);
        int level = search.Level ?? n - 1;
        File.Move(PathOf(level), pending, overwrite: true);
        levelFiles.Remove(PathOf(level));
        return Finish(s, copy, target, pending, search, level, ReduceMode.Raster, original, progress);
    }

    /// <summary>pending에 있는 선택한 결과를 검증하고, 목표를 맞췄으면 target으로 옮긴다. 못 맞췄으면 pending으로 남긴다.</summary>
    private static ReduceResult Finish(DocumentSession s, string copy, string target, string pending, LadderSearch search, int level,
        ReduceMode mode, long original, IProgress<ReduceProgress>? progress)
    {
        progress?.Report(new ReduceProgress("verify", 0.9));
        if (!OutputVerifier.Verify(s.Thread, copy, pending, null, mode == ReduceMode.Raster ? SizeLadder.Raster[level].MaxDpi : null))
            throw new ReduceVerificationException();
        long size = new FileInfo(pending).Length;
        if (search.Level is null)
            return new ReduceResult(false, false, original, size, search.BytesAtStrongest, mode, level, pending);
        File.Move(pending, target, overwrite: true);
        progress?.Report(new ReduceProgress("verify", 1.0));
        return new ReduceResult(false, true, original, size, search.BytesAtStrongest, mode, level, null);
    }

    /// <summary>
    /// 흑백 변환의 화질 3등급(<see cref="SizeLadder.Mono"/>)을 모두 만들어 크기를 알려 준다. 사용자가 하나를 고르면 <see cref="CommitMonochrome"/>, 아니면 <see cref="DiscardMonochrome"/>.
    /// 쪽을 1비트 JBIG2 그림으로 바꾸므로 색·음영·글자 객체가 사라진다. 각 결과는 150dpi에서 원본을 같은 흑백 기준으로 바꾼 것과 비교해 검증하고, 어긋나는 등급은 뺀다.
    /// </summary>
    public async Task<MonoVariants> BuildMonochromeVariantsAsync(DocumentSession s, string targetPath, IProgress<ReduceProgress>? progress, CancellationToken ct)
    {
        if (s.IsEncrypted) throw new NotSupportedException("Encrypted documents are not supported.");
        string target = Path.GetFullPath(targetPath);
        if (s.FilePath is { } fp && string.Equals(Path.GetFullPath(fp), target, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The target must differ from the source file.", nameof(targetPath));

        string copy = target + ".copy.tmp";
        var files = new List<string>();
        try
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ReduceProgress("copy", 0));
            await builder.BuildAsync(s, copy, ct).ConfigureAwait(false);
            long copyBytes = new FileInfo(copy).Length;
            long original = s.FilePath is { } sp && File.Exists(sp) ? Math.Max(new FileInfo(sp).Length, copyBytes) : copyBytes;

            var ladder = SizeLadder.Mono;
            var items = new List<MonoVariant>();
            for (int i = 0; i < ladder.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                int index = i;
                string path = target + $".m{ladder[i].MaxDpi}.tmp";
                files.Add(path);
                double baseFraction = 0.05 + 0.8 * i / ladder.Count, span = 0.8 / ladder.Count;
                var pageProgress = new Progress<double>(f => progress?.Report(new ReduceProgress("mono", baseFraction + span * f)));
                await Task.Run(() => Rasterizer.BuildMonochrome(s.Thread, copy, null, path, ladder[index], ct, pageProgress), ct).ConfigureAwait(false);
                items.Add(new MonoVariant(ladder[i].MaxDpi, new FileInfo(path).Length, path));
            }

            progress?.Report(new ReduceProgress("verify", 0.9));
            var verified = new List<MonoVariant>();
            foreach (var v in items)
            {
                ct.ThrowIfCancellationRequested();
                if (await Task.Run(() => OutputVerifier.Verify(s.Thread, copy, v.Path, null, VerifyDpi, binarize: true), ct).ConfigureAwait(false)) verified.Add(v);
                else TryDelete(v.Path);
            }
            if (verified.Count == 0) throw new ReduceVerificationException();
            files.Clear();   // 이제 호출자가 소유한다
            progress?.Report(new ReduceProgress("verify", 1.0));
            return new MonoVariants(verified, original);
        }
        catch
        {
            foreach (var f in files) TryDelete(f);
            throw;
        }
        finally { TryDelete(copy); }
    }

    /// <summary>고른 등급(Items의 번호)을 targetPath로 옮기고 나머지는 지운다.</summary>
    public void CommitMonochrome(MonoVariants v, int index, string targetPath)
    {
        File.Move(v.Items[index].Path, Path.GetFullPath(targetPath), overwrite: true);
        for (int i = 0; i < v.Items.Count; i++) if (i != index) TryDelete(v.Items[i].Path);
    }

    /// <summary>모든 등급의 임시 결과를 버린다.</summary>
    public void DiscardMonochrome(MonoVariants v)
    {
        foreach (var item in v.Items) TryDelete(item.Path);
    }

    private const int VerifyDpi = 150;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
