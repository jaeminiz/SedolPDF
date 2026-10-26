using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Document;
using PdfEditor.Document.Content;
using PdfEditor.TestFixtures;
using PdfEditor.TextEdit.Paragraphs;
using PdfSharp.Pdf.IO;

namespace PdfEditor.TextEdit.Tests.Paragraphs;

/// <summary>M5 Task 5: ParagraphReader(클릭 줄 주변 읽기) + ParagraphEditPlanner(편집 창 글 → ReplaceParagraphEdit) + 화면 replay.</summary>
public sealed class ParagraphPlannerTests : IDisposable
{
    private static readonly Lazy<AppFont> Font = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        return AppFont.Load(Path.Combine(dir!.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"));
    });

    private readonly PdfiumThread _t = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-m5task5-" + Guid.NewGuid().ToString("N"));

    public ParagraphPlannerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<(DocumentSession S, PageId Page, ParagraphModel M)> OpenParagraph(string sample, int minLines = 3, string? path = null)
    {
        var s = await DocumentSession.OpenAsync(_t, path ?? ParagraphTestData.Sample(sample).Path, null);
        var page = s.Pages[0].Id;
        var m = await _t.InvokeAsync(() =>
        {
            var idx = s.GetObjectIndex(page);
            foreach (var e in idx.Entries.Where(e => e.Type == PdfPageObjectType.Text && !e.Ref.IsInserted).ToList())
                if (ParagraphReader.Detect(s, page, e.Ref, idx).Model is { IsParagraph: true } mm && mm.Lines.Length >= minLines) return mm;
            return null;
        });
        Assert.NotNull(m);
        return (s, page, m!);
    }

    /// <summary>문단 가운데 줄의 낱말 하나를 글자 순서만 뒤집은 글(같은 길이). 못 만들면 null.</summary>
    private static (string Edited, string Old, string New, int Line)? ReverseWord(ParagraphModel m)
    {
        string orig = ParagraphLayout.OriginalText(m);
        for (int li = 1; li < m.Lines.Length - 1; li++)
        {
            foreach (var w in m.Lines[li].Text.Split(' '))
            {
                if (w.Length < 4 || w.Distinct().Count() <= 2 || !w.All(char.IsLetter)) continue;
                string rev = new(w.Reverse().ToArray());
                int at = orig.IndexOf(w, StringComparison.Ordinal);
                int lineStart = orig.IndexOf(m.Lines[li].Text, StringComparison.Ordinal);
                if (rev == w || lineStart < 0 || at != lineStart + m.Lines[li].Text.IndexOf(w, StringComparison.Ordinal)) continue;
                return (orig[..at] + rev + orig[(at + w.Length)..], w, rev, li);
            }
        }
        return null;
    }

    private Task<ParagraphPlan> Plan(DocumentSession s, PageId page, ParagraphModel m, string text, AppFont? font, ReplaceParagraphEdit? previous = null) =>
        _t.InvokeAsync(() => ParagraphEditPlanner.Plan(s, page, m, text, font, previous));

    private Task<string> PageText(DocumentSession s, PageId page) => _t.InvokeAsync(() =>
    {
        s.GetReplay(page);
        var tp = s.GetLoadedText(page);
        return tp.GetText(0, tp.CharCount);
    });

    private static string Squash(string t) => string.Concat(t.Where(c => !char.IsWhiteSpace(c)));

    [Fact]
    public async Task PlanFromModel_NoChange_NoEdit()
    {
        var (s, page, m) = await OpenParagraph("doc-en-left");
        var plan = await Plan(s, page, m, ParagraphLayout.OriginalText(m), Font.Value);
        Assert.True(plan.Ok);
        Assert.True(plan.NoChange);
        Assert.Null(plan.Edit);
        Assert.Null(plan.ToCommand(page));
        Assert.False(s.Content.HasEdits);
    }

    [Theory]
    [InlineData("doc-en-left")]
    [InlineData("doc-ko-left-wordwrap")]
    public async Task SameLengthWordChange_OriginalFont_OnlyThatLineReplaced(string sample)
    {
        var (s, page, m) = await OpenParagraph(sample);
        var ed = ReverseWord(m);
        Assert.NotNull(ed);
        var plan = await Plan(s, page, m, ed!.Value.Edited, Font.Value);
        Assert.True(plan.Ok);
        Assert.NotNull(plan.Edit);
        Assert.False(plan.UsedAppFont);
        var e = plan.Edit!;
        Assert.Single(e.OriginalFontLines);
        Assert.Empty(e.AppLines);
        Assert.Equal(m.Lines[ed.Value.Line].AllRefs.OrderBy(r => r.OriginalIndex), e.Remove.OrderBy(r => r.OriginalIndex));
        Assert.Contains(e.OriginalFontLines[0].Text, ed.Value.Edited);
    }

    [Fact]
    public async Task Replay_ParagraphEdit_RenderMatchesLayout_And_SearchFindsReplacedText()
    {
        var (s, page, m) = await OpenParagraph("doc-en-left");
        var ed = ReverseWord(m)!.Value;
        var plan = await Plan(s, page, m, ed.Edited, Font.Value);
        await s.Commands.ExecuteAsync(plan.ToCommand(page)!);
        string text = Squash(await PageText(s, page));
        Assert.Contains(Squash(plan.Edit!.OriginalFontLines[0].Text), text);
        Assert.DoesNotContain(Squash(m.Lines[ed.Line].Text), text);

        // 화면 모양: 새 줄이 계획한 위치(기준선 원점)에 있다
        var line = plan.Edit.OriginalFontLines[0];
        var inserted = await _t.InvokeAsync(() => s.GetReplay(page).Resolve(ObjectRef.Inserted(line.LineId)));
        Assert.NotNull(inserted);
        var mat = await _t.InvokeAsync(() => inserted!.Matrix);
        Assert.Equal(line.Placement.E, mat.E, 2);
        Assert.Equal(line.Placement.F, mat.F, 2);
    }

    [Fact]
    public async Task Replay_RemovedLinesInactive_UnchangedLinesUntouched()
    {
        var (s, page, m) = await OpenParagraph("doc-en-left");
        var ed = ReverseWord(m)!.Value;
        var plan = await Plan(s, page, m, ed.Edited, Font.Value);
        await s.Commands.ExecuteAsync(plan.ToCommand(page)!);
        var (removedActive, othersActive) = await _t.InvokeAsync(() =>
        {
            var r = s.GetReplay(page);
            bool anyRemovedActive = plan.Edit!.Remove.Any(rf => r.Resolve(rf)!.IsActive);
            var keep = m.Lines.Where((_, i) => i != ed.Line).SelectMany(l => l.AllRefs);
            bool allOthers = keep.All(rf => r.Resolve(rf)!.IsActive);
            return (anyRemovedActive, allOthers);
        });
        Assert.False(removedActive);
        Assert.True(othersActive);
    }

    [Fact]
    public async Task Undo_ParagraphEdit_RestoresOriginalLines_Redo_SameResult()
    {
        var (s, page, m) = await OpenParagraph("doc-en-left");
        var ed = ReverseWord(m)!.Value;
        string before = Squash(await PageText(s, page));
        var plan = await Plan(s, page, m, ed.Edited, Font.Value);
        await s.Commands.ExecuteAsync(plan.ToCommand(page)!);
        string after = Squash(await PageText(s, page));
        Assert.NotEqual(before, after);

        await s.Commands.UndoAsync();
        Assert.Equal(before, Squash(await PageText(s, page)));
        Assert.False(s.Content.HasEdits);

        await s.Commands.RedoAsync();
        Assert.Equal(after, Squash(await PageText(s, page)));
    }

    [Fact]
    public async Task ReEdit_ReplacesPreviousEdit_NoStacking()
    {
        var (s, page, m) = await OpenParagraph("doc-en-left");
        var ed = ReverseWord(m)!.Value;
        var first = await Plan(s, page, m, ed.Edited, Font.Value);
        await s.Commands.ExecuteAsync(first.ToCommand(page)!);

        // 같은 낱말을 다른 글자 순서로 한 번 더 고친다 (저장된 원본 문단 모델로 — 재추정 없음)
        string again = ed.Edited.Replace(ed.New, new string(ed.Old.Reverse().Skip(1).Concat(ed.Old.Reverse().Take(1)).ToArray()));
        var second = await _t.InvokeAsync(() => ParagraphEditPlanner.PlanReEdit(s, page, first.Edit!, again, Font.Value));
        Assert.True(second.Ok);
        Assert.Same(first.Edit, second.Previous);
        await s.Commands.ExecuteAsync(second.ToCommand(page)!);
        Assert.Single(s.Content.For(page).OfType<ReplaceParagraphEdit>());

        // 원래 글로 돌리면 이전 편집이 빠진다
        var back = await _t.InvokeAsync(() => ParagraphEditPlanner.PlanReEdit(s, page, second.Edit!, ParagraphLayout.OriginalText(m), Font.Value));
        Assert.True(back.NoChange);
        await s.Commands.ExecuteAsync(back.ToCommand(page)!);
        Assert.Empty(s.Content.For(page).OfType<ReplaceParagraphEdit>());

        // 실행 취소는 직전 상태로
        await s.Commands.UndoAsync();
        Assert.Single(s.Content.For(page).OfType<ReplaceParagraphEdit>());
        Assert.Same(second.Edit, s.Content.For(page).OfType<ReplaceParagraphEdit>().Single());
        await s.Commands.UndoAsync();
        Assert.Same(first.Edit, s.Content.For(page).OfType<ReplaceParagraphEdit>().Single());
    }

    [Fact]
    public async Task UnsupportedChar_RewrittenLinesUseAppFont()
    {
        var (s, page, m) = await OpenParagraph("doc-en-left");
        string orig = ParagraphLayout.OriginalText(m);
        // 서브셋에 없는 글자(한글)를 넣는다 → 다시 쓰는 줄 전체가 앱 글꼴
        int at = orig.IndexOf(' ', 10);
        string edited = orig[..at] + " 가나다 " + orig[(at + 1)..];
        var plan = await Plan(s, page, m, edited, Font.Value);
        Assert.True(plan.Ok);
        Assert.True(plan.UsedAppFont);
        Assert.NotEmpty(plan.Edit!.AppLines);
        Assert.Empty(plan.Edit.OriginalFontLines);
        Assert.Equal(plan.Edit.AppLines.Length, plan.Edit.AppLineIds.Length);

        await s.Commands.ExecuteAsync(plan.ToCommand(page)!);
        // 앱 글꼴 줄은 화면에서 윤곽선 경로 객체로 들어간다
        int paths = await _t.InvokeAsync(() => plan.Edit.AppLineIds.Count(id => s.GetReplay(page).Resolve(ObjectRef.Inserted(id)) is { Type: PdfPageObjectType.Path }));
        Assert.Equal(plan.Edit.AppLines.Length, paths);
    }

    [Fact]
    public async Task UnsupportedChar_NoAppFont_Refused()
    {
        var (s, page, m) = await OpenParagraph("doc-en-left");
        string orig = ParagraphLayout.OriginalText(m);
        var plan = await Plan(s, page, m, "가나다 " + orig, null);
        Assert.False(plan.Ok);
        Assert.Contains(plan.Refusal, new[] { ParagraphRefusal.NoAppFont, ParagraphRefusal.MissingGlyphs });
        Assert.Null(plan.ToCommand(page));
    }

    [Fact]
    public async Task EmptyText_DeletesWholeParagraph()
    {
        var (s, page, m) = await OpenParagraph("doc-en-left");
        var plan = await Plan(s, page, m, "  ", Font.Value);
        Assert.True(plan.Ok);
        Assert.Equal(m.AllRefs.OrderBy(r => r.OriginalIndex), plan.Edit!.Remove.OrderBy(r => r.OriginalIndex));
        Assert.Empty(plan.Edit.OriginalFontLines);
        Assert.Empty(plan.Edit.AppLines);
    }

    [Fact]
    public async Task PageBottomOverflow_Refused()
    {
        var (s, page, m) = await OpenParagraph("doc-en-left");
        string orig = ParagraphLayout.OriginalText(m);
        string huge = string.Join(" ", Enumerable.Repeat(orig, 60));
        var plan = await Plan(s, page, m, huge, Font.Value);
        Assert.Equal(ParagraphRefusal.Overflow, plan.Refusal);
        Assert.Null(plan.ToCommand(page));
    }

    [Fact]
    public async Task Encrypted_ParagraphEditRefused_SingleLineStillWorks()
    {
        string src = ParagraphTestData.Sample("doc-en-left").Path;
        string p = Path.Combine(_dir, "enc.pdf");
        using (var d = PdfReader.Open(src, PdfDocumentOpenMode.Modify))
        {
            d.SecuritySettings.UserPassword = "pw";
            d.SecuritySettings.OwnerPassword = "pw-o";
            d.Save(p);
        }
        var s = await DocumentSession.OpenAsync(_t, p, "pw");
        var page = s.Pages[0].Id;
        var m = await _t.InvokeAsync(() =>
        {
            var idx = s.GetObjectIndex(page);
            foreach (var e in idx.Entries.Where(e => e.Type == PdfPageObjectType.Text).ToList())
                if (ParagraphReader.Detect(s, page, e.Ref, idx).Model is { IsParagraph: true } mm) return mm;
            return null;
        });
        Assert.NotNull(m);
        var plan = await Plan(s, page, m!, "something else", Font.Value);
        Assert.Equal(ParagraphRefusal.Encrypted, plan.Refusal);

        // 한 줄 편집(M4)은 암호 문서에서도 된다
        var seed = m!.Lines[0].Runs[0].Ref;
        var line = await _t.InvokeAsync(() => TextEditPlanner.PlanReplace(s, page, seed, m.Lines[0].Text, Font.Value));
        Assert.Equal(ReplaceRefusal.None, line.Refusal);
    }

    [Fact]
    public async Task Reader_PicksOnlyNearbyCandidates()
    {
        string p = Path.Combine(_dir, "many.pdf");
        ContentPdfFactory.CreateManyTextLines(p, 5000);
        var s = await DocumentSession.OpenAsync(_t, p, null);
        var page = s.Pages[0].Id;
        var input = await _t.InvokeAsync(() =>
        {
            var idx = s.GetObjectIndex(page);
            var mid = idx.Entries.Where(e => e.Type == PdfPageObjectType.Text).OrderBy(e => e.Order).ElementAt(2500).Ref;
            return ParagraphReader.Read(s, page, mid, idx);
        });
        Assert.Equal(ParagraphReadRefusal.None, input.Refusal);
        Assert.InRange(input.ObjectsRead, 1, ParagraphReader.MaxObjects + 10);
        Assert.True(input.Runs.Length <= ParagraphReader.MaxObjects + 10);
    }

    [Fact]
    public async Task Reader_FormChild_NotEditable_Message()
    {
        var s = await DocumentSession.OpenAsync(_t, ParagraphTestData.Sample("dwg-form-text").Path, null);
        var page = s.Pages[0].Id;
        var input = await _t.InvokeAsync(() =>
        {
            var idx = s.GetObjectIndex(page);
            var form = idx.Entries.FirstOrDefault(e => e.Type == PdfPageObjectType.Form);
            return form is null ? null : ParagraphReader.Read(s, page, form.Ref, idx);
        });
        Assert.NotNull(input);
        Assert.Equal(ParagraphReadRefusal.InForm, input!.Refusal);
        Assert.Empty(input.Runs);
    }

    [Fact]
    public async Task Reader_OverlaidBold_EmptyTwinObjectBecomesDuplicate()
    {
        var s = await DocumentSession.OpenAsync(_t, ParagraphTestData.Sample("doc-overlay-bold").Path, null);
        var page = s.Pages[0].Id;
        var det = await _t.InvokeAsync(() =>
        {
            var idx = s.GetObjectIndex(page);
            foreach (var e in idx.Entries.Where(e => e.Type == PdfPageObjectType.Text).ToList())
            {
                var d = ParagraphReader.Detect(s, page, e.Ref, idx);
                if (d.Model is { IsParagraph: true }) return d;
            }
            return null;
        });
        Assert.NotNull(det);
        Assert.All(det!.Model!.Lines, l => Assert.NotEmpty(l.Duplicates));
        // 끄기 대상에 겹친 복사본도 들어간다
        Assert.True(det.Model.AllRefs.Count() >= det.Model.Lines.Length * 2);
    }

    [Fact]
    public async Task DecideKorean_WordWrapVsCharWrap()
    {
        foreach (var (name, expect) in new[] { ("doc-ko-left-wordwrap", KoreanLineBreak.Word), ("doc-ko-charwrap", KoreanLineBreak.Character) })
        {
            var s = await DocumentSession.OpenAsync(_t, ParagraphTestData.Sample(name).Path, null);
            var page = s.Pages[0].Id;
            var got = await _t.InvokeAsync(() =>
            {
                var idx = s.GetObjectIndex(page);
                var runs = new List<TextRun>();
                var obs = new List<Obstacle>();
                foreach (var e in idx.Entries.Where(e => e.Type == PdfPageObjectType.Text).ToList())
                {
                    var inp = ParagraphReader.Read(s, page, e.Ref, idx);
                    runs.AddRange(inp.Runs.Where(r => !runs.Any(x => x.Ref == r.Ref)));
                    obs.AddRange(inp.Obstacles);
                }
                var lines = LineAssembler.Assemble(runs, obs);
                return ParagraphEditPlanner.DecideKorean(lines, obs);
            });
            Assert.Equal(expect, got);
        }
    }

    // ---- M5 Task 8: 대체 글꼴 카탈로그(고딕/명조) 연결 ----

    private static AppFontCatalog Catalog()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        return AppFontCatalog.ForBaseDirectory(Font.Value, dir!.FullName);
    }

    private string MakeFontParagraph(string family, bool bold = false)
    {
        _ = ParagraphTestData.Sample("doc-en-left");   // 글꼴 해석기 준비(공용 샘플 생성기가 설정한다)
        string path = Path.Combine(_dir, family.Replace(' ', '_') + (bold ? "-b" : "") + ".pdf");
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        page.Width = PdfSharp.Drawing.XUnit.FromPoint(595);
        page.Height = PdfSharp.Drawing.XUnit.FromPoint(842);
        using (var g = PdfSharp.Drawing.XGraphics.FromPdfPage(page))
        {
            var f = new PdfSharp.Drawing.XFont(family, 11, bold ? PdfSharp.Drawing.XFontStyleEx.Bold : PdfSharp.Drawing.XFontStyleEx.Regular,
                new PdfSharp.Drawing.XPdfFontOptions(PdfSharp.Pdf.PdfFontEncoding.Unicode));
            string[] lines =
            [
                "The commissioning of the system shall be witnessed by the owner",
                "representative and the classification surveyor before the final",
                "acceptance test, and the records shall be kept on board for the",
                "whole service life of the vessel.",
            ];
            for (int i = 0; i < lines.Length; i++) g.DrawString(lines[i], f, PdfSharp.Drawing.XBrushes.Black, new PdfSharp.Drawing.XPoint(60, 90 + i * 15), PdfSharp.Drawing.XStringFormats.BaseLineLeft);
        }
        doc.Save(path);
        return path;
    }

    [Fact]
    public async Task SerifOriginal_AppFontFallback_UsesMyeongjo()
    {
        var (s, page, m) = await OpenParagraph("", 3, MakeFontParagraph("Georgia"));
        var catalog = Catalog();
        Assert.NotNull(catalog.Serif);
        string edited = ParagraphLayout.OriginalText(m) + " 한글 추가";   // 서브셋에 없는 글자 → 앱 글꼴로 내려감
        var plan = await _t.InvokeAsync(() => ParagraphEditPlanner.Plan(s, page, m, edited, Font.Value, catalog: catalog));
        Assert.True(plan.Ok, plan.Refusal.ToString());
        Assert.True(plan.UsedAppFont);
        Assert.NotEmpty(plan.Edit!.AppLines);
        Assert.All(plan.Edit.AppLines, l => Assert.Equal(PdfEditor.Document.Annotations.Patching.FontUse.SerifFamily, l.Font.Family));
    }

    [Fact]
    public async Task SansOriginal_AppFontFallback_UsesGothic_BoldIsSynthetic()
    {
        var (s, page, m) = await OpenParagraph("", 3, MakeFontParagraph("Arial", bold: true));
        var catalog = Catalog();
        string edited = ParagraphLayout.OriginalText(m) + " 한글 추가";
        var plan = await _t.InvokeAsync(() => ParagraphEditPlanner.Plan(s, page, m, edited, Font.Value, catalog: catalog));
        Assert.True(plan.Ok, plan.Refusal.ToString());
        Assert.NotEmpty(plan.Edit!.AppLines);
        Assert.All(plan.Edit.AppLines, l => Assert.Equal("", l.Font.Family));
        Assert.All(plan.Edit.AppLines, l => Assert.Contains("2 Tr", l.Content));   // 굵은 원본 → 가짜 굵게(렌더 모드 2)
    }
}
