using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Appearance;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Document.Tests.Annotations;

public class AnnotationFlushTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-aflush-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly PdfAppearanceSource _ap = new();

    public AnnotationFlushTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    private async Task<DocumentSession> OpenMixed()
    {
        AnnotatedPdfFactory.CreateMixed(P("mixed.pdf"));
        var s = await DocumentSession.OpenAsync(_t, P("mixed.pdf"), null);
        await s.AnnotationsReady;
        return s;
    }

    private Task Flush(DocumentSession s) => s.Thread.InvokeAsync(() => s.FlushAnnotations(_ap));

    private async Task<string> FlushAndSave(DocumentSession s, string name)
    {
        var path = P(name);
        await s.Thread.InvokeAsync(() => { s.FlushAnnotations(_ap); s.Document.SaveCopy(path); });
        return path;
    }

    private static Annotation Mk(DocumentSession s, AnnotKind kind, PdfRectF rect, string tag) => new()
    {
        Id = s.Annotations.NewId(),
        Kind = kind,
        Rect = rect,
        Color = new PdfColor(200, 30, 20),
        FillColor = kind is AnnotKind.Square or AnnotKind.Circle ? new PdfColor(10, 120, 240) : null,
        Opacity = 0.5f,
        BorderWidth = kind is AnnotKind.Ink or AnnotKind.Square or AnnotKind.Circle ? 2f : 1f,
        InkStrokes = kind == AnnotKind.Ink
            ? [[new PdfPointF(110, 110), new PdfPointF(130, 150), new PdfPointF(160, 120)]]
            : [],
        Quads = kind.IsTextMarkup()
            ? [new PdfQuadF(new(rect.Left, rect.Top), new(rect.Right, rect.Top), new(rect.Left, rect.Bottom), new(rect.Right, rect.Bottom))]
            : [],
        Contents = "내용 " + tag,
        Author = "작성자",
        Subject = "주제 " + tag,
        Name = Guid.NewGuid().ToString("D"),
        Flags = PdfAnnotFlags.Print,
    };

    private static async Task Exec(DocumentSession s, PageId page, Annotation? before, Annotation? after)
        => await s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(page, [new AnnotationChange(before, after)]));

    private sealed record Raw(string Subtype, HashSet<string> Keys, int Flags, double[] Rect, byte[]? Ap, string? XKeep, string? Nm, string? Rc, string? Contents);

    private static List<Raw> ReadRaw(string path, int pageIndex = 0)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var arr = doc.Pages[pageIndex].Elements.GetArray("/Annots");
        var list = new List<Raw>();
        if (arr is null) return list;
        foreach (var item in arr.Elements)
        {
            var d = item is PdfReference r ? (PdfDictionary)r.Value : (PdfDictionary)item;
            var rect = d.Elements.GetRectangle("/Rect");
            var n = d.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N");
            list.Add(new Raw(
                d.Elements.GetName("/Subtype").TrimStart('/'),
                [.. d.Elements.Keys],
                d.Elements.GetInteger("/F"),
                [rect.X1, rect.Y1, rect.X2, rect.Y2],
                n?.Stream?.UnfilteredValue,
                d.Elements.GetString("/XKeep") is { Length: > 0 } x ? x : null,
                d.Elements.GetString("/NM") is { Length: > 0 } nm ? nm : null,
                d.Elements.GetString("/RC") is { Length: > 0 } rc ? rc : null,
                d.Elements.GetString("/Contents") is { Length: > 0 } c ? c : null));
        }
        return list;
    }

    private static IReadOnlyList<OriginalAnnotation> ReadModel(string path, int pageIndex = 0)
        => AnnotationReader.Read(File.ReadAllBytes(path), null, [pageIndex])[0];

    [Fact]
    public async Task NewAnnotations_AllKinds_FlushSaveReopen_ReadBackEquivalent()
    {
        await using var s = await DocumentSession.CreateNewAsync(_t);
        var page = s.Pages[0].Id;
        var made = new List<Annotation>();
        int k = 0;
        foreach (var kind in Enum.GetValues<AnnotKind>())
        {
            var a = Mk(s, kind, kind == AnnotKind.Text ? new PdfRectF(100, 400, 120, 420) : new PdfRectF(100 + 10 * k, 100 + 30 * k, 200 + 10 * k, 130 + 30 * k), kind.ToString());
            made.Add(a);
            await Exec(s, page, null, a);
            k++;
        }
        Assert.Equal(7, made.Count);
        var path = await FlushAndSave(s, "all.pdf");

        var back = ReadModel(path);
        Assert.Equal(7, back.Count);
        for (int i = 0; i < 7; i++)
        {
            Assert.Equal(ReadOnlyReason.None, back[i].Reason);
            Assert.True(AnnotationComparer.Equivalent(made[i], back[i].Data!), $"{made[i].Kind} differs: {made[i]} VS {back[i].Data}");
        }
        foreach (var r in ReadRaw(path))
        {
            Assert.True((r.Flags & PdfAnnotFlags.Print) != 0);
            Assert.NotNull(r.Nm);
            Assert.NotNull(r.Ap);
            Assert.True(r.Ap!.Length > 0);
        }
    }

    [Fact]
    public async Task OwnAppearance_NoPdfiumGeneratedApKey()
    {
        await using var s = await DocumentSession.CreateNewAsync(_t);
        var page = s.Pages[0].Id;
        foreach (var kind in Enum.GetValues<AnnotKind>())
            await Exec(s, page, null, Mk(s, kind, new PdfRectF(100, 100, 150, 140), kind.ToString()));
        var path = await FlushAndSave(s, "keys.pdf");
        foreach (var r in ReadRaw(path))
        {
            Assert.Contains("/AP", r.Keys);
            Assert.DoesNotContain("/PDFIUM_HasGeneratedAP", r.Keys);
        }
    }

    [Fact]
    public async Task TouchedOriginal_UnknownKeysPreserved()
    {
        await using var s = await OpenMixed();
        var page = s.Pages[0].Id;
        var before = ReadRaw(P("mixed.pdf"));
        var sq = s.Annotations.Get(page).Items[0].Data;
        var moved = sq with { Rect = new PdfRectF(150, 650, 350, 700) };
        await Exec(s, page, sq, moved);
        var path = await FlushAndSave(s, "touched.pdf");

        var after = ReadRaw(path);
        Assert.Equal(before.Count, after.Count);
        Assert.Equal("abc", after[1].XKeep);
        Assert.Contains("/BS", after[1].Keys);
        Assert.Equal(150, after[1].Rect[0], 0.01);
        Assert.Equal(700, after[1].Rect[3], 0.01);
        Assert.NotNull(after[1].Ap);
        Assert.False(before[1].Ap!.SequenceEqual(after[1].Ap!));
    }

    [Fact]
    public async Task UntouchedOriginal_DictionaryAndApUnchanged()
    {
        await using var s = await OpenMixed();
        var page = s.Pages[0].Id;
        var before = ReadRaw(P("mixed.pdf"));
        var sq = s.Annotations.Get(page).Items[0].Data;
        await Exec(s, page, sq, sq with { Rect = new PdfRectF(150, 650, 350, 700) });
        var path = await FlushAndSave(s, "untouched.pdf");

        var after = ReadRaw(path);
        for (int i = 0; i < before.Count; i++)
        {
            if (i == 1) continue;
            Assert.Equal(before[i].Subtype, after[i].Subtype);
            Assert.True(before[i].Keys.SetEquals(after[i].Keys), $"[{i}] keys differ: {string.Join(",", before[i].Keys)} vs {string.Join(",", after[i].Keys)}");
            Assert.Equal(before[i].Flags, after[i].Flags);
            Assert.Equal(before[i].Rect, after[i].Rect);
            Assert.Equal(before[i].Ap, after[i].Ap);
            Assert.Equal(before[i].Contents, after[i].Contents);
        }
    }

    [Fact]
    public async Task DeleteOriginalText_RemovesItsPopupAndReplies()
    {
        await using var s = await OpenMixed();
        var page = s.Pages[0].Id;
        var text = s.Annotations.Get(page).Items.Select(i => i.Data).Single(a => a.Kind == AnnotKind.Text);
        await Exec(s, page, text, null);
        var path = await FlushAndSave(s, "deltext.pdf");

        var after = ReadRaw(path);
        Assert.Equal(10, after.Count);
        Assert.DoesNotContain(after, r => r.Subtype == "Text");
        Assert.DoesNotContain(after, r => r.Subtype == "Popup");
        Assert.Equal(new[] { "Link", "Square", "Highlight", "Square", "Ink", "Underline", "StrikeOut", "Circle", "Square", "Square" },
            after.Select(r => r.Subtype));
    }

    [Fact]
    public async Task ContentsChange_OnOriginalWithRc_RcRewritten()
    {
        await using var s = await OpenMixed();
        var page = s.Pages[0].Id;
        var text = s.Annotations.Get(page).Items.Select(i => i.Data).Single(a => a.Kind == AnnotKind.Text);
        await Exec(s, page, text, text with { Contents = "고친 메모 <&>" });
        var path = await FlushAndSave(s, "rc.pdf");

        var after = ReadRaw(path);
        Assert.Equal(13, after.Count);
        Assert.Contains("고친 메모", after[3].Rc);
        Assert.Contains("&lt;&amp;&gt;", after[3].Rc);
        Assert.Equal("고친 메모 <&>", after[3].Contents);
        Assert.Equal("고친 메모 <&>", AnnotationReader.Read(File.ReadAllBytes(path), null, [0])[0].Single(o => o.Subtype == PdfAnnotSubtype.Text && o.Data is not null).Data!.Contents);
    }

    [Fact]
    public async Task Flush_Twice_SecondIsNoOp()
    {
        await using var s = await DocumentSession.CreateNewAsync(_t);
        var page = s.Pages[0].Id;
        await Exec(s, page, null, Mk(s, AnnotKind.Square, new PdfRectF(10, 10, 60, 60), "a"));
        await Flush(s);
        Assert.False(await s.Thread.InvokeAsync(() => s.Annotations.HasUnflushedChanges));
        await Flush(s);
        var path = await FlushAndSave(s, "twice.pdf");
        Assert.Single(ReadRaw(path));
    }

    [Fact]
    public async Task FlushThenUndoThenFlush_IndicesStayConsistent()
    {
        await using var s = await OpenMixed();
        var page = s.Pages[0].Id;
        var sq = Mk(s, AnnotKind.Square, new PdfRectF(10, 10, 60, 60), "n");
        await Exec(s, page, null, sq);
        await Flush(s);
        Assert.Equal(14, ReadRaw(await FlushAndSave(s, "a1.pdf")).Count);

        await s.Commands.UndoAsync();
        await Flush(s);
        Assert.Equal(13, ReadRaw(await FlushAndSave(s, "a2.pdf")).Count);

        await s.Commands.RedoAsync();
        await Flush(s);
        Assert.Equal(14, ReadRaw(await FlushAndSave(s, "a3.pdf")).Count);

        // 원본 [1] 이동 → Flush → 취소 → Flush: 원래 위치
        var orig = s.Annotations.Get(page).Items[0].Data;
        await Exec(s, page, orig, orig with { Rect = new PdfRectF(400, 400, 450, 450) });
        var moved = ReadRaw(await FlushAndSave(s, "b1.pdf"));
        Assert.Equal(400, moved[1].Rect[0], 0.01);
        await s.Commands.UndoAsync();
        var restored = ReadRaw(await FlushAndSave(s, "b2.pdf"));
        Assert.Equal(14, restored.Count);
        Assert.Equal(100, restored[1].Rect[0], 0.01);
        Assert.Equal(300, restored[1].Rect[2], 0.01);
        Assert.Equal("abc", restored[1].XKeep);
        Assert.Equal("Square", restored[13].Subtype);
        Assert.Equal(10, restored[13].Rect[0], 0.01);
    }

    [Fact]
    public async Task AddedThenDeleted_BeforeAnyFlush_LeavesNoPdfAnnotation()
    {
        await using var s = await OpenMixed();
        var page = s.Pages[0].Id;
        var sq = Mk(s, AnnotKind.Square, new PdfRectF(10, 10, 60, 60), "n");
        await Exec(s, page, null, sq);
        await Exec(s, page, sq, null);
        Assert.False(await s.Thread.InvokeAsync(() => s.Annotations.HasUnflushedChanges));
        var path = await FlushAndSave(s, "addel.pdf");
        Assert.Equal(13, ReadRaw(path).Count);
    }

    [Fact]
    public async Task DeleteOriginal_Flush_UndoThenFlush_ReAddsIt()
    {
        await using var s = await OpenMixed();
        var page = s.Pages[0].Id;
        var sq = s.Annotations.Get(page).Items[0].Data;
        await Exec(s, page, sq, null);
        Assert.Equal(12, ReadRaw(await FlushAndSave(s, "d1.pdf")).Count);
        await s.Commands.UndoAsync();
        var path = await FlushAndSave(s, "d2.pdf");
        var after = ReadRaw(path);
        Assert.Equal(13, after.Count);
        Assert.Equal("Square", after[12].Subtype);
        Assert.Equal(100, after[12].Rect[0], 0.01);
        Assert.Equal(300, after[12].Rect[2], 0.01);
        Assert.True((after[12].Flags & PdfAnnotFlags.Print) != 0);
        // 지운 뒤 번호가 당겨져도 나머지 원본 주석은 그대로
        Assert.Equal("Highlight", after[1].Subtype);
    }

    [Fact]
    public async Task HiddenIndicesAfterFlush_PointToOwnedAnnotations()
    {
        await using var s = await OpenMixed();
        var page = s.Pages[0].Id;
        var items = s.Annotations.Get(page).Items.Select(i => i.Data).ToList();
        var square = items.Single(a => a.Kind == AnnotKind.Square && a.Rect.Left == 100 && a.Rect.Bottom == 600);
        var text = items.Single(a => a.Kind == AnnotKind.Text);
        var ink = items.Single(a => a.Kind == AnnotKind.Ink);
        await Exec(s, page, square, square with { Rect = new PdfRectF(1, 1, 50, 50) });
        await Exec(s, page, text, null);
        await Exec(s, page, ink, ink with { BorderWidth = 4 });
        var added = Mk(s, AnnotKind.Circle, new PdfRectF(10, 10, 60, 60), "c");
        await Exec(s, page, null, added);
        await Flush(s);

        var (hidden, subtypes) = await s.Thread.InvokeAsync(() =>
        {
            var h = s.Annotations.HiddenPdfIndices(page).OrderBy(x => x).ToArray();
            var p = s.GetLoadedPage(page);
            return (h, h.Select(i => { using var a = p.GetAnnotation(i); return a.Subtype; }).ToArray());
        });
        // 손댄 원본 2개 + 저장으로 PDF에 들어간 새 Circle(맨 끝). 셋 다 주석 층이 그리므로 타일에서 숨긴다.
        Assert.Equal(3, hidden.Length);
        Assert.Contains(PdfAnnotSubtype.Square, subtypes);
        Assert.Contains(PdfAnnotSubtype.Ink, subtypes);
        Assert.Contains(PdfAnnotSubtype.Circle, subtypes);
        Assert.Equal(1, hidden[0]);   // Square는 번호 1 그대로
        Assert.Equal(4, hidden[1]);   // Ink [7]: Text·Popup·답글 3개가 빠져 4
    }

    [Fact]
    public async Task NewAnnotation_AfterFlush_HiddenInTiles_EvenAfterDelete()
    {
        // 저장(Flush) 뒤에는 새 주석도 PDFium 문서에 들어 있다. 그 주석은 주석 층(ModelDrawn)이 그리므로
        // 타일에서는 숨겨야 한다: 안 숨기면 두 번 그려지고(반투명이면 진해짐), 저장 뒤 지운 주석이 타일에 남는다.
        await using var s = await OpenMixed();
        var page = s.Pages[0].Id;
        var added = Mk(s, AnnotKind.Highlight, new PdfRectF(10, 10, 60, 60), "h");
        await Exec(s, page, null, added);
        await Flush(s);
        var idx = await s.Thread.InvokeAsync(() => s.GetLoadedPage(page).AnnotationCount - 1);
        Assert.True(s.Annotations.Get(page).Items.Single(i => i.Data.Id == added.Id).ModelDrawn);
        Assert.Contains(idx, await s.Thread.InvokeAsync(() => s.Annotations.HiddenPdfIndices(page)));

        await Exec(s, page, added, null);   // 저장 뒤 지움: 다음 Flush 전까지 PDFium에는 남아 있다
        Assert.Contains(idx, await s.Thread.InvokeAsync(() => s.Annotations.HiddenPdfIndices(page)));
    }

    [Fact]
    public async Task DeletedPageInStash_SkippedUntilRestored()
    {
        TestPdfFactory.CreateLabeled(P("two.pdf"), [(300, 400), (300, 400)]);
        await using var s = await DocumentSession.OpenAsync(_t, P("two.pdf"), null);
        await s.AnnotationsReady;
        var p1 = s.Pages[1].Id;
        await Exec(s, p1, null, Mk(s, AnnotKind.Square, new PdfRectF(10, 10, 60, 60), "n"));
        await s.Commands.ExecuteAsync(new DeletePagesCommand([p1]));

        await Flush(s);   // 쪽이 문서에 없으므로 건너뜀
        Assert.True(await s.Thread.InvokeAsync(() => s.Annotations.HasUnflushedChanges));

        await s.Commands.UndoAsync();
        await Flush(s);
        Assert.False(await s.Thread.InvokeAsync(() => s.Annotations.HasUnflushedChanges));
        var path = await FlushAndSave(s, "restored.pdf");
        Assert.Single(ReadRaw(path, 1));
        Assert.Empty(ReadRaw(path, 0));
    }

    [Fact]
    public async Task CompleteFlush_MissingIndexForSurvivingEntry_Throws()
    {
        await using var s = await OpenMixed();
        var page = s.Pages[0].Id;
        var sq = s.Annotations.Get(page).Items[0].Data;
        await Exec(s, page, sq, sq with { Rect = new PdfRectF(1, 1, 2, 2) });
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Thread.InvokeAsync(() =>
            s.Annotations.CompleteFlush(page, new Dictionary<AnnotEntry, int>())));
    }
}
