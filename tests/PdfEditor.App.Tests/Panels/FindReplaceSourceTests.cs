using PdfEditor.Document;
using PdfEditor.Document.Saving;
using PdfEditor.Pages;
using PdfEditor.TextEdit.FindReplace;
using PdfEditor.TextEdit.Tests;
using PdfEditor.App.Tests.Viewer.Objects;
using Xunit.Abstractions;

namespace PdfEditor.App.Tests.Panels;

/// <summary>
/// 0.4.0 Task 6 실제 도면 결함: 글이 있는데 "먼저 저장하세요"(no-file / page-structure-changed)만 뜬 경우의 원인을 모두 재현한다.
/// 찾기는 어떤 문서 상태에서도 현재 화면의 글을 찾아야 한다(원본 파일이 없거나 쪽 구성이 바뀐 경우 포함).
/// </summary>
[Collection("TabSettings")]
public class FindReplaceSourceTests(ITestOutputHelper log) : ObjectUiTestBase
{
    private const string Old = DrawingIdPdfFactory.Id;
    private SaveService Svc => new([], Services.Appearance, Services.Patcher, Services.Assets);

    private async Task<DocumentSession> Open(int pages = 2, string? keep = null)
    {
        return await OpenAsync(p =>
        {
            string one = p + ".one";
            DrawingIdPdfFactory.Create(one, IdCase.Whole, Old, true);
            using var src = PdfSharp.Pdf.IO.PdfReader.Open(one, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            using var dst = new PdfSharp.Pdf.PdfDocument();
            for (int i = 0; i < pages; i++) dst.AddPage(src.Pages[0]);
            dst.Save(p);
        });
    }

    private static async Task<string> Probe(DocumentSession s)
    {
        try
        {
            using var f = await FindReplaceService.FindAsync(s, new FindOptions { Query = Old, MatchCase = true, WholeWord = true });
            return $"찾음 {f.Matches.Count()}";
        }
        catch (InvalidOperationException ex) { return "예외 " + ex.Message; }
    }

    [Fact]
    public async Task EveryDocumentState_FindsTheVisibleText()
    {
        var rows = new List<(string Case, string Result, int Expected)>();
        async Task Check(string name, DocumentSession s, int expected) => rows.Add((name, await Probe(s), expected));

        var a = await Open(); await Check("열자마자", a, 6);

        var b = await Open();
        await b.Commands.ExecuteAsync(new RotatePagesCommand([b.Pages[0].Id], 1)); await Check("쪽 회전 뒤(저장 전)", b, 6);

        var c = await Open();
        await c.Commands.ExecuteAsync(new InsertBlankPageCommand(1, 595, 842)); await Check("빈 쪽 삽입 뒤(저장 전)", c, 6);

        var d = await Open();
        await d.Commands.ExecuteAsync(new DeletePagesCommand([d.Pages[1].Id])); await Check("쪽 삭제 뒤(저장 전)", d, 3);

        var e = await Open();
        await e.Commands.ExecuteAsync(new MovePagesCommand([e.Pages[0].Id], 1)); await Check("쪽 이동 뒤(저장 전)", e, 6);

        var f = await Open();
        await f.Commands.ExecuteAsync(new DeletePagesCommand([f.Pages[1].Id]));
        await Svc.SaveAsync(f, Path.Combine(Dir, "f-saved.pdf")); await Check("쪽 삭제 → 저장 뒤", f, 3);

        var g = await Open();
        await Svc.SaveAsync(g, Path.Combine(Dir, "g-as.pdf")); await Check("다른 이름으로 저장 뒤", g, 6);

        var h = await Open();
        File.Delete(h.FilePath!); await Check("원본 파일이 지워진 뒤(메일 첨부·임시 폴더)", h, 6);

        var i = await Open();
        File.Move(i.FilePath!, i.FilePath + ".moved"); await Check("원본 파일을 옮긴 뒤", i, 6);

        var j = await Open();
        await j.Commands.ExecuteAsync(new DeletePagesCommand([j.Pages[1].Id]));
        File.Delete(j.FilePath!); await Check("쪽 삭제 + 원본 없음", j, 3);

        // 같은 이름의 다른 파일로 바뀐 경우(쪽 수는 같고 내용이 다름)
        var k = await Open(2);
        File.Delete(k.FilePath!); TestFixtures_Blank(k.FilePath!, 2); await Check("원본 자리에 다른 파일(같은 쪽 수)", k, 6);

        foreach (var r in rows) log.WriteLine($"{r.Case,-40} {r.Result}");
        foreach (var r in rows) Assert.Equal($"찾음 {r.Expected}", r.Result);
    }

    private static void TestFixtures_Blank(string path, int pages) => PdfEditor.TestFixtures.TestPdfFactory.CreateBlank(path, pages, 595, 842);

    /// <summary>실제 매뉴얼(private/, 저장소에 없음 — 파일이 없으면 건너뜀)로 "VIS"를 찾는다: 열자마자 / 쪽 삭제 뒤 / 원본이 없어진 뒤가 모두 같은 방식으로 찾아진다. 수치만 기록.</summary>
    [Fact]
    public async Task RealManual_VisIsFound_InEveryDocumentState()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? priv = null;
        while (dir is not null) { var p = Path.Combine(dir.FullName, "private"); if (Directory.Exists(p)) { priv = p; break; } dir = dir.Parent; }
        var file = priv is null ? null : Directory.GetFiles(priv, "*VIS-200*.pdf").FirstOrDefault();
        if (file is null) return;
        int Hits(FindOutcome f) => f.Matches.Count();
        var opts = new FindOptions { Query = "VIS", MatchCase = true };

        var s = await OpenAsync(p => File.Copy(file, p));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var f0 = await FindReplaceService.FindAsync(s, opts, null);
        long ms = sw.ElapsedMilliseconds;
        int baseHits = Hits(f0);
        log.WriteLine($"실제 매뉴얼 {s.Pages.Length}쪽: 열자마자 'VIS' {baseHits}곳 ({ms} ms), 찾을 수 없는 글 연산 {f0.Report.UnsearchableOps}개");
        Assert.True(baseHits > 0);

        File.Delete(s.FilePath!);
        using var f1 = await FindReplaceService.FindAsync(s, opts, null);
        Assert.Equal(baseHits, Hits(f1));

        await s.Commands.ExecuteAsync(new DeletePagesCommand([s.Pages[^1].Id]));
        using var f2 = await FindReplaceService.FindAsync(s, opts, null);
        log.WriteLine($"원본 삭제 + 마지막 쪽 삭제 뒤 'VIS' {Hits(f2)}곳 (전: {baseHits})");
        Assert.True(Hits(f2) <= baseHits && Hits(f2) > 0);
    }
}
