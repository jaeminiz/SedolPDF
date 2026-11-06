using System.Security.Cryptography;
using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Output;
using PdfEditor.TestFixtures;

namespace PdfEditor.Optimize.Tests;

public sealed class SizeReducerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-reduce-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public SizeReducerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    private static SizeReducer NewReducer() => new(new OutputCopyBuilder(null, null, null));

    private Task<DocumentSession> OpenAsync(string path, string? password = null) => DocumentSession.OpenAsync(_t, path, password);

    private string ThreeNoisyPages(string name = "noisy.pdf")
    {
        string src = P(name);
        ImagePdfFactory.Create(src,
            new ImageSpec(1200, 800, ImageKind.FlateRgb, 300, 200),
            new ImageSpec(1200, 800, ImageKind.FlateRgb, 300, 200),
            new ImageSpec(1200, 800, ImageKind.FlateRgb, 300, 200));
        return src;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private Task<int> PageCount(string path) => _t.InvokeAsync(() =>
    {
        using var d = Pdf.Native.PdfDocument.Open(_t, path, null);
        return d.PageCount;
    });

    private sealed class SyncProgress(Action<ReduceProgress> on) : IProgress<ReduceProgress>
    {
        public void Report(ReduceProgress value) => on(value);
    }

    [Fact]
    public async Task Reduce_LargeImages_AchievesTarget_OriginalUntouched()
    {
        string src = ThreeNoisyPages();
        string before = Hash(src);
        long srcBytes = new FileInfo(src).Length;
        await using var s = await OpenAsync(src);
        string target = P("noisy_small.pdf");
        long limit = 1_000_000;

        var r = await NewReducer().ReduceAsync(s, target, limit, ReduceMode.Images, null, CancellationToken.None);

        Assert.True(srcBytes > limit * 3, "fixture should be much larger than the target");
        Assert.False(r.AlreadySmallEnough);
        Assert.True(r.Achieved);
        Assert.True(r.FinalBytes <= limit, r.FinalBytes.ToString());
        Assert.Equal(new FileInfo(target).Length, r.FinalBytes);
        Assert.Null(r.PendingPath);
        Assert.Equal(3, await PageCount(target));
        Assert.Equal(before, Hash(src));   // 원본 불변
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Reduce_TargetAboveCurrent_AlreadySmall_WritesNothing()
    {
        string src = ThreeNoisyPages();
        await using var s = await OpenAsync(src);
        string target = P("never.pdf");
        var r = await NewReducer().ReduceAsync(s, target, 1_000_000_000, ReduceMode.Images, null, CancellationToken.None);
        Assert.True(r.AlreadySmallEnough);
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Reduce_FileBloatedByIncrementalUpdate_CopyAlreadyFits_WritesTheCopy()
    {
        // 디스크의 파일은 증분 저장으로 부풀어 목표를 넘지만, 다시 쓴 사본은 목표 안이다 → "이미 작다"가 아니라 사본을 저장한다.
        string baseline = P("base.pdf");
        ImagePdfFactory.Create(baseline, new ImageSpec(400, 300, ImageKind.FlateRgb, 200, 150));
        string src = P("bloated.pdf");
        File.WriteAllBytes(src, PdfAppend.UnreferencedObject(File.ReadAllBytes(baseline), 300_000));
        await using var s = await OpenAsync(src);
        string probe = P("probe.tmp.pdf");
        await new OutputCopyBuilder(null, null, null).BuildAsync(s, probe);
        long copyBytes = new FileInfo(probe).Length;
        long srcBytes = new FileInfo(src).Length;
        File.Delete(probe);
        Assert.True(copyBytes + 100_000 < srcBytes, $"PDFium's copy should drop the unreferenced object: {copyBytes} vs {srcBytes}");

        string target = P("bloated_small.pdf");
        var r = await NewReducer().ReduceAsync(s, target, copyBytes + 1000, ReduceMode.Images, null, CancellationToken.None);
        Assert.False(r.AlreadySmallEnough);
        Assert.True(r.Achieved);
        Assert.Equal(-1, r.Level);
        Assert.Equal(srcBytes, r.OriginalBytes);
        Assert.True(File.Exists(target));
        Assert.True(new FileInfo(target).Length <= copyBytes + 1000);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Reduce_VectorOnly_NotAchieved_PendingSet_TargetNotCreated()
    {
        string src = P("blank.pdf");
        TestPdfFactory.CreateBlank(src, 1, 200, 200);
        await using var s = await OpenAsync(src);
        string target = P("blank_small.pdf");
        var r = await NewReducer().ReduceAsync(s, target, 100, ReduceMode.Images, null, CancellationToken.None);
        Assert.False(r.AlreadySmallEnough);
        Assert.False(r.Achieved);
        Assert.NotNull(r.PendingPath);
        Assert.True(File.Exists(r.PendingPath));
        Assert.False(File.Exists(target));
        Assert.True(r.MinPossibleBytes > 100);
    }

    [Fact]
    public async Task Reduce_NotAchieved_Commit_WritesPending_Discard_RemovesIt()
    {
        string src = P("blank2.pdf");
        TestPdfFactory.CreateBlank(src, 1, 200, 200);
        await using var s = await OpenAsync(src);
        var reducer = NewReducer();

        string t1 = P("keep.pdf");
        var r1 = await reducer.ReduceAsync(s, t1, 100, ReduceMode.Images, null, CancellationToken.None);
        reducer.Commit(r1, t1);
        Assert.True(File.Exists(t1));
        Assert.False(File.Exists(r1.PendingPath!));

        string t2 = P("drop.pdf");
        var r2 = await reducer.ReduceAsync(s, t2, 100, ReduceMode.Images, null, CancellationToken.None);
        reducer.Discard(r2);
        Assert.False(File.Exists(t2));
        Assert.False(File.Exists(r2.PendingPath!));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Reduce_SameAsOriginalPath_Throws()
    {
        string src = ThreeNoisyPages();
        await using var s = await OpenAsync(src);
        await Assert.ThrowsAsync<ArgumentException>(() => NewReducer().ReduceAsync(s, src, 1000, ReduceMode.Images, null, CancellationToken.None));
    }

    [Fact]
    public async Task Reduce_Encrypted_Throws()
    {
        string src = P("enc.pdf");
        TestPdfFactory.CreateEncrypted(src, 1, 200, 200, "pw");
        await using var s = await OpenAsync(src, "pw");
        await Assert.ThrowsAsync<NotSupportedException>(() => NewReducer().ReduceAsync(s, P("enc_small.pdf"), 100, ReduceMode.Images, null, CancellationToken.None));
    }

    [Fact]
    public async Task Reduce_CancelledMidway_LeavesNoTempFiles()
    {
        string src = ThreeNoisyPages();
        await using var s = await OpenAsync(src);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.Stage == "images") cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewReducer().ReduceAsync(s, P("cancel_small.pdf"), 1_000_000, ReduceMode.Images, progress, cts.Token));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.False(File.Exists(P("cancel_small.pdf")));
    }

    [Fact]
    public async Task Reduce_KoreanSpacedPath_Works()
    {
        string folder = Path.Combine(_dir, "95. 클로드 폴더");
        Directory.CreateDirectory(folder);
        string src = Path.Combine(folder, "원본 파일.pdf");
        ImagePdfFactory.Create(src, new ImageSpec(1200, 800, ImageKind.FlateRgb, 300, 200));
        await using var s = await OpenAsync(src);
        string target = Path.Combine(folder, "원본 파일_축소.pdf");
        var r = await NewReducer().ReduceAsync(s, target, 400_000, ReduceMode.Images, null, CancellationToken.None);
        Assert.True(r.Achieved);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task Reduce_RasterMode_PageCountMatches_AchievesTarget()
    {
        string src = ThreeNoisyPages();
        await using var s = await OpenAsync(src);
        string target = P("raster.pdf");
        long limit = 400_000;
        var r = await NewReducer().ReduceAsync(s, target, limit, ReduceMode.Raster, null, CancellationToken.None);
        Assert.Equal(ReduceMode.Raster, r.Mode);
        Assert.True(r.Achieved, $"min possible {r.MinPossibleBytes}");
        Assert.True(r.FinalBytes <= limit);
        Assert.Equal(3, await PageCount(target));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    private string TenColorPages()
    {
        string src = P("ten.pdf");
        TestPdfFactory.CreateLabeled(src, Enumerable.Repeat((595f, 842f), 10).ToList());
        return src;
    }

    [Fact]
    public async Task EstimateConversions_ThreeLayeredGradesHighestFirst_AndCloseToTheRealSize_AndLeavesNoTempFiles()
    {
        await using var s = await OpenAsync(TenColorPages());
        var reducer = NewReducer();
        var est = await reducer.EstimateConversionsAsync(s, P("est.pdf"), null, CancellationToken.None);

        Assert.Equal([600, 300, 200], est.Layered.Select(g => g.Dpi).ToArray());
        Assert.True(est.ColorImagesBytes > 0);
        Assert.All(est.Layered, g => Assert.True(g.Bytes > 0));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        // 실제로 300dpi 등급을 만들어 예상과 비교한다(열 쪽이 같은 모양이라 오차가 작다).
        var r = await reducer.ReduceAsync(s, P("real.pdf"), 1, ReduceMode.Layered, null, CancellationToken.None, level: 1);
        double ratio = (double)est.Layered[1].Bytes / r.FinalBytes;
        Assert.InRange(ratio, 0.7, 1.3);
    }

    [Fact]
    public async Task Layered_WritesTheChosenGradeEvenWhenOverTheTarget_WithSamePageCount_AndNoTempFiles()
    {
        await using var s = await OpenAsync(TenColorPages());
        string target = P("layered.pdf");
        var r = await NewReducer().ReduceAsync(s, target, 1, ReduceMode.Layered, null, CancellationToken.None, level: 2);
        Assert.Equal(ReduceMode.Layered, r.Mode);
        Assert.True(r.Achieved);
        Assert.Equal(2, r.Level);
        Assert.Equal(10, await PageCount(target));
        Assert.Equal(new FileInfo(target).Length, r.FinalBytes);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Layered_Cancelled_LeavesNoTemporaryFiles()
    {
        await using var s = await OpenAsync(TenColorPages());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewReducer().ReduceAsync(s, P("c.pdf"), 1, ReduceMode.Layered, null, cts.Token, level: 0));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.False(File.Exists(P("c.pdf")));
    }
}
