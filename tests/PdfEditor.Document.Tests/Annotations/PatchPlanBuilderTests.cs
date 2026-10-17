using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Appearance;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.Document.Saving;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Document.Tests.Annotations;

public class PatchPlanBuilderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-ppb-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly PdfAppearanceSource _ap = new();

    public PatchPlanBuilderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    private SaveService Svc() => new([], _ap, new PdfSharpAnnotationPatcher(() => []));

    private async Task<DocumentSession> OpenMixed()
    {
        AnnotatedPdfFactory.CreateMixed(P("mixed.pdf"));
        var s = await DocumentSession.OpenAsync(_t, P("mixed.pdf"), null);
        await s.AnnotationsReady;
        return s;
    }

    private static Annotation NewNote(DocumentSession s) => new()
    {
        Id = s.Annotations.NewId(),
        Kind = AnnotKind.Text,
        Rect = new PdfRectF(100, 400, 120, 420),
        Color = new PdfColor(255, 200, 0),
        Contents = "새 메모",
        Author = "작성자",
        Flags = PdfAnnotFlags.Print,
    };

    private static Task Exec(DocumentSession s, PageId page, Annotation? before, Annotation? after)
        => s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(page, [new AnnotationChange(before, after)]));

    private sealed record RawAnnot(string Subtype, string[] Keys, string? XKeep, bool HasPopup, bool HasIrt, bool HasBs, bool HasIc);

    private static List<RawAnnot> ReadRaw(string path, string? pw = null)
    {
        using var doc = pw is null ? PdfReader.Open(path, PdfDocumentOpenMode.Import) : PdfReader.Open(path, pw, PdfDocumentOpenMode.Import);
        var item = doc.Pages[0].Elements["/Annots"];
        var arr = (PdfArray?)((item as PdfReference)?.Value ?? item);
        var list = new List<RawAnnot>();
        if (arr is null) return list;
        foreach (var e in arr.Elements)
        {
            var d = (PdfDictionary)((e as PdfReference)?.Value ?? e);
            var keys = d.Elements.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            list.Add(new RawAnnot(d.Elements.GetName("/Subtype").TrimStart('/'), keys,
                d.Elements.GetString("/XKeep") is { Length: > 0 } x ? x : null,
                d.Elements.ContainsKey("/Popup"), d.Elements.ContainsKey("/IRT"),
                d.Elements.ContainsKey("/BS"), d.Elements.ContainsKey("/IC")));
        }
        return list;
    }

    private static int CountOf(List<RawAnnot> l, string subtype) => l.Count(a => a.Subtype == subtype);

    private Task<PatchPlan> Build(DocumentSession s, bool flush = true)
        => s.Thread.InvokeAsync(() =>
        {
            if (flush) s.FlushAnnotations(_ap);
            return PatchPlanBuilder.Build(s);
        });

    [Fact]
    public async Task NewText_Save_HasPopupOnce_ReopenThenSaveAgain_StillOne()
    {
        await using (var s = await DocumentSession.CreateNewAsync(_t))
        {
            await Exec(s, s.Pages[0].Id, null, NewNote(s));
            await Svc().SaveAsync(s, P("a.pdf"));
            await Svc().SaveAsync(s, P("a2.pdf"));   // 같은 모델에서 두 번째 저장
        }
        foreach (var f in new[] { "a.pdf", "a2.pdf" })
        {
            var raw = ReadRaw(P(f));
            Assert.Equal(2, raw.Count);
            Assert.Equal(1, CountOf(raw, "Popup"));
            Assert.True(raw.Single(r => r.Subtype == "Text").HasPopup);
        }

        // 다시 열면 그 메모는 Popup이 있는 원본이다: 건드려도, 안 건드려도 Popup은 하나.
        await using (var s = await DocumentSession.OpenAsync(_t, P("a.pdf"), null))
        {
            await s.AnnotationsReady;
            var note = s.Annotations.Get(s.Pages[0].Id).Items.Select(i => i.Data).Single(a => a.Kind == AnnotKind.Text);
            await Svc().SaveAsync(s, P("b.pdf"));
            await Exec(s, s.Pages[0].Id, note, note with { Contents = "고침" });
            await Svc().SaveAsync(s, P("c.pdf"));
        }
        foreach (var f in new[] { "b.pdf", "c.pdf" })
        {
            var raw = ReadRaw(P(f));
            Assert.Equal(2, raw.Count);
            Assert.Equal(1, CountOf(raw, "Popup"));
        }
    }

    [Fact]
    public async Task Builder_EnsurePopupOnlyForNewNotesWithoutPopup_NoDuplicateIndices()
    {
        await using var s = await OpenMixed();
        var page = s.Pages[0].Id;
        var text = s.Annotations.Get(page).Items.Select(i => i.Data).Single(a => a.Kind == AnnotKind.Text);
        await Exec(s, page, text, text with { Contents = "손댄 원본 메모" });   // 원본(Popup 있음): 만들지 않는다
        await Exec(s, page, null, NewNote(s));                                   // 새 메모: 하나
        var plan = await Build(s);

        Assert.Single(plan.Patches.SelectMany(p => p.Ops).OfType<EnsurePopup>());
        var popup = plan.Patches.Single(p => p.Ops.OfType<EnsurePopup>().Any());
        Assert.Equal("Text", popup.Subtype);
        Assert.Equal(13, popup.AnnotIndex);   // 원본 13개 뒤
        Assert.Equal(new PdfRectF(124, 300, 304, 420), popup.Ops.OfType<EnsurePopup>().Single().Rect);   // (Right+4, Top-120, +180, Top)
        Assert.False(popup.Ops.OfType<EnsurePopup>().Single().Open);
        Assert.Equal(1, plan.CountDelta(0));

        // 두 번째 Build도 같은 계획 (상태 없음)
        var again = await Build(s);
        Assert.Equal(plan.Patches.Length, again.Patches.Length);
        Assert.Equal(1, again.CountDelta(0));
    }

    [Fact]
    public async Task Builder_DeletedOriginal_RemovalIndicesDistinctDescending()
    {
        await using var s = await OpenMixed();
        var page = s.Pages[0].Id;
        var text = s.Annotations.Get(page).Items.Select(i => i.Data).Single(a => a.Kind == AnnotKind.Text);
        var link = s.Annotations.Get(page).Items.Select(i => i.Data).Where(a => a.Kind == AnnotKind.Square).First();
        await Exec(s, page, text, null);
        await Exec(s, page, link, null);
        var plan = await Build(s);

        var r = Assert.Single(plan.Removals);
        Assert.Equal(r.AnnotIndices.OrderByDescending(i => i), r.AnnotIndices);   // 큰 번호부터
        Assert.Equal(r.AnnotIndices.Distinct().Count(), r.AnnotIndices.Length);
        Assert.Equal(new[] { 5, 4, 3, 1 }, r.AnnotIndices);                       // 답글[5] Popup[4] Text[3] Square[1]
        Assert.Equal(-4, plan.CountDelta(0));
    }

    [Fact]
    public void CountDelta_DuplicateRemovalIndices_CountedOnce()
    {
        var plan = new PatchPlan([], [], [new AnnotationRemoval(0, [3, 3, 1])]);
        Assert.Equal(-2, plan.CountDelta(0));
    }

    [Fact]
    public async Task DeleteOriginalSaveUndoSave_OriginalRestoredExactly()
    {
        await using var s = await OpenMixed();
        var page = s.Pages[0].Id;
        var before = ReadRaw(P("mixed.pdf"));
        var text = s.Annotations.Get(page).Items.Select(i => i.Data).Single(a => a.Kind == AnnotKind.Text);
        var sq = s.Annotations.Get(page).Items.Select(i => i.Data).First(a => a.Kind == AnnotKind.Square);

        await Exec(s, page, text, null);
        await Exec(s, page, sq, null);
        await Svc().SaveAsync(s, P("s1.pdf"));
        var s1 = ReadRaw(P("s1.pdf"));
        Assert.Equal(9, s1.Count);
        Assert.Equal(0, CountOf(s1, "Text"));        // 지운 메모·답글 모두 없음
        Assert.Equal(0, CountOf(s1, "Popup"));
        Assert.DoesNotContain(s1, a => a.XKeep == "abc");

        await s.Commands.UndoAsync();
        await s.Commands.UndoAsync();
        await Svc().SaveAsync(s, P("s2.pdf"));
        var s2 = ReadRaw(P("s2.pdf"));

        Assert.Equal(before.Count, s2.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Subtype, s2[i].Subtype);
            Assert.Equal(before[i].Keys, s2[i].Keys);   // 원시 키 집합 그대로
            Assert.Equal(before[i].XKeep, s2[i].XKeep);
            Assert.Equal(before[i].HasPopup, s2[i].HasPopup);
            Assert.Equal(before[i].HasIrt, s2[i].HasIrt);
        }
        Assert.Equal("abc", s2[1].XKeep);
        Assert.True(s2[3].HasPopup);
        Assert.True(s2[5].HasIrt);
        // 세 번째 저장도 같다 (패치는 상태가 없다)
        await Svc().SaveAsync(s, P("s3.pdf"));
        Assert.Equal(before.Select(b => b.Keys), ReadRaw(P("s3.pdf")).Select(a => a.Keys));
    }

    [Fact]
    public async Task DeleteOriginal_Encrypted_FallsBackToPdfiumRemoval()
    {
        var p = P("enc.pdf");
        AnnotatedPdfFactory.CreateEncryptedWithAnnotations(p, "pw");
        await using (var s = await DocumentSession.OpenAsync(_t, p, "pw"))
        {
            await s.AnnotationsReady;
            var page = s.Pages[0].Id;
            var text = s.Annotations.Get(page).Items.Select(i => i.Data).Single(a => a.Kind == AnnotKind.Text);
            await Exec(s, page, text, null);
            Assert.False(SavePolicy.CanPatch(s));
            await Svc().SaveAsync(s, P("out.pdf"));
            // M2 방식: PDFium에서 직접 지움 → 항목이 남지 않는다
            Assert.True(await s.Thread.InvokeAsync(() => s.Annotations.HiddenPdfIndices(page).Count == 0));
            Assert.True((await Build(s, flush: false)).IsEmpty);
        }
        await using var again = await DocumentSession.OpenAsync(_t, P("out.pdf"), "pw");
        Assert.True(again.IsEncrypted);
        Assert.Equal(1, await again.Thread.InvokeAsync(() => again.GetLoadedPage(again.Pages[0].Id).AnnotationCount));   // Square만
    }

    [Fact]
    public async Task TouchedSquareWithBs_SavedWithoutBs_BorderWidthKept()
    {
        await using var s = await OpenMixed();
        var page = s.Pages[0].Id;
        var sq = s.Annotations.Get(page).Items.Select(i => i.Data).First(a => a.Kind == AnnotKind.Square);
        Assert.True(ReadRaw(P("mixed.pdf"))[1].HasBs);
        Assert.Equal(3f, sq.BorderWidth);
        await Exec(s, page, sq, sq with { Rect = new PdfRectF(110, 610, 310, 660) });
        await Svc().SaveAsync(s, P("o.pdf"));

        var raw = ReadRaw(P("o.pdf"));
        Assert.False(raw[1].HasBs);
        Assert.Contains("/Border", raw[1].Keys);
        Assert.Equal("abc", raw[1].XKeep);
        var back = AnnotationReader.Read(File.ReadAllBytes(P("o.pdf")), null, [0])[0][1];
        Assert.Equal(ReadOnlyReason.None, back.Reason);
        Assert.Equal(3f, back.Data!.BorderWidth);
    }

    [Fact]
    public async Task RemovedFill_IcRemoved()
    {
        await using var s = await OpenMixed();
        var page = s.Pages[0].Id;
        var circle = s.Annotations.Get(page).Items.Select(i => i.Data).Single(a => a.Kind == AnnotKind.Circle);
        Assert.NotNull(circle.FillColor);
        Assert.True(ReadRaw(P("mixed.pdf"))[10].HasIc);
        var unfilled = circle with { FillColor = null };
        await Exec(s, page, circle, unfilled);
        await Svc().SaveAsync(s, P("o.pdf"));

        Assert.False(ReadRaw(P("o.pdf"))[10].HasIc);
        var back = AnnotationReader.Read(File.ReadAllBytes(P("o.pdf")), null, [0])[0][10];
        Assert.Null(back.Data!.FillColor);

        // 채우기를 바꾸기만 했다면 /IC는 남는다
        await Exec(s, page, unfilled, unfilled with { FillColor = new PdfColor(1, 2, 3) });
        await Svc().SaveAsync(s, P("o2.pdf"));
        Assert.True(ReadRaw(P("o2.pdf"))[10].HasIc);
    }

    [Fact]
    public async Task DeletedOriginal_StaysHiddenInTiles()
    {
        await using var s = await OpenMixed();
        var page = s.Pages[0].Id;
        var text = s.Annotations.Get(page).Items.Select(i => i.Data).Single(a => a.Kind == AnnotKind.Text);
        await Exec(s, page, text, null);
        Assert.Contains(3, await s.Thread.InvokeAsync(() => s.Annotations.HiddenPdfIndices(page)));   // Flush 전
        await Svc().SaveAsync(s, P("o.pdf"));

        // Flush 뒤에도 PDFium에는 남아 있고(13개) 항목은 Deleted로 남아 타일에서 계속 숨긴다.
        Assert.Equal(13, await s.Thread.InvokeAsync(() => s.GetLoadedPage(page).AnnotationCount));
        Assert.Contains(3, await s.Thread.InvokeAsync(() => s.Annotations.HiddenPdfIndices(page)));
        Assert.DoesNotContain(s.Annotations.Get(page).Items, i => i.Data.Kind == AnnotKind.Text);

        // 되살리면 손대지 않은 원본이므로 PDFium이 다시 그린다(숨김 해제).
        await s.Commands.UndoAsync();
        Assert.DoesNotContain(3, await s.Thread.InvokeAsync(() => s.Annotations.HiddenPdfIndices(page)));
        Assert.False(s.Annotations.Get(page).Items.Single(i => i.Data.Kind == AnnotKind.Text).ModelDrawn);
    }
}
