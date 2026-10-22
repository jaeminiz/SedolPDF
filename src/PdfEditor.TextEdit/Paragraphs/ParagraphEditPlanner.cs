using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.ContentStream;
using PdfEditor.ContentStream.Fonts;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.Document.Saving;
using PdfSharp.Pdf.IO;

namespace PdfEditor.TextEdit.Paragraphs;

/// <summary>문단 편집을 계획할 수 없는 이유(화면 문구는 앱이 리소스에서 고른다).</summary>
public enum ParagraphRefusal
{
    None,
    /// <summary>암호 문서 — 문단 편집은 끈다(한 줄 편집 M4는 된다).</summary>
    Encrypted,
    /// <summary>문단(두 줄 이상, 확신 문턱 이상)이 아니다 — 한 줄 편집으로.</summary>
    NotParagraph,
    /// <summary>앱 글꼴이 필요한데 없다.</summary>
    NoAppFont,
    /// <summary>어느 글꼴로도 그릴 수 없는 글자가 있다(<see cref="ParagraphPlan.Missing"/>).</summary>
    MissingGlyphs,
    /// <summary>쪽 아래 경계를 넘는 줄이 생긴다(C6: 겹침은 허용, 쪽 밖은 확정 불가).</summary>
    Overflow,
}

/// <summary>
/// 문단 편집 계획. <see cref="Edit"/>가 있으면 <see cref="ToCommand"/>로 실행한다. Edit가 없고 <see cref="NoChange"/>이면 바뀐 것이 없다
/// (이전 편집 <see cref="Previous"/>가 있으면 그것을 되돌리는 명령이 된다).
/// </summary>
public sealed record ParagraphPlan(ReplaceParagraphEdit? Edit, ReplaceParagraphEdit? Previous, ParagraphLayoutResult? Layout, bool NoChange,
    bool UsedAppFont, bool FontsMixed, bool Overlap, ParagraphRefusal Refusal, IReadOnlyList<int> Missing)
{
    public static ParagraphPlan Refused(ParagraphRefusal r, IReadOnlyList<int>? missing = null, ParagraphLayoutResult? layout = null) =>
        new(null, null, layout, false, false, false, false, r, missing ?? []);

    public bool Ok => Refusal == ParagraphRefusal.None;

    /// <summary>실행할 명령. 할 일이 없으면(바뀐 것 없고 되돌릴 이전 편집도 없음, 또는 거절) null.</summary>
    public ParagraphEditCommand? ToCommand(PageId page) =>
        !Ok || (Edit is null && Previous is null) ? null : new ParagraphEditCommand(page, Previous, Edit);
}

/// <summary>원래 글꼴로 새 글을 인코딩할 코드북을 만든다(M5 C8). 못 만들면 null(+이유) — 그 문단은 대체 글꼴로 간다.</summary>
public interface IParagraphFontSource
{
    FontCodebook? Build(DocumentSession s, PageId page, ParagraphModel p, out double tc, out double tz, out string? reason);
}

/// <summary>
/// 세션의 원본 파일을 PDFsharp로 읽어 글 객체 번호 → 글꼴 사전·보인 문자열을 얻는다(<see cref="PageTextFonts"/>, V16 규칙으로 번호가 맞는 쪽만).
/// 쪽을 옮기거나 끼워 넣어 파일과 쪽 번호가 어긋나면 객체 종류 열이 달라 거절된다(안전 쪽: 대체 글꼴). 파일이 없는 새 문서·암호 문서는 항상 null.
/// 문서를 열고 닫는 비용이 있으므로 부르는 쪽이 쪽 버전마다 한 번만 부르게 캐시한다(Task 8).
/// </summary>
public sealed class SessionFileFontSource : IParagraphFontSource
{
    public FontCodebook? Build(DocumentSession s, PageId page, ParagraphModel p, out double tc, out double tz, out string? reason)
    {
        tc = 0; tz = 100; reason = null;
        if (s.FilePath is null || !File.Exists(s.FilePath) || s.IsEncrypted) { reason = "no-file"; return null; }
        int index = s.IndexOf(page);
        var r = s.GetReplay(page);
        var kinds = r.Originals.Select(o => (ObjectKind)(int)o.Type).ToList();
        try
        {
            using var doc = PdfReader.Open(s.FilePath, PdfDocumentOpenMode.Import);
            if (index < 0 || index >= doc.PageCount) { reason = "page-mismatch"; return null; }
            var map = PageTextFonts.Build(doc.Pages[index], kinds, out var failure);
            if (map is null) { reason = failure ?? "object-map"; return null; }

            var runs = p.Lines.SelectMany(l => l.Runs).Where(x => !x.Ref.IsInserted).ToList();
            var infos = new List<(TextRun Run, TextObjectFont Font)>();
            foreach (var run in runs)
            {
                if (!map.TryGetValue(run.Ref.OriginalIndex, out var f)) { reason = "run-not-in-stream"; return null; }
                infos.Add((run, f));
            }
            if (infos.Count == 0) { reason = "no-runs"; return null; }
            var first = infos[0].Font.Font;
            foreach (var (_, f) in infos)
                if (!SameDict(first, f.Font)) { reason = "mixed-font-dicts"; return null; }

            var shown = infos.SelectMany(x => x.Font.Strings).ToList();
            var learn = infos.Select(x => ((ReadOnlyMemory<byte>)x.Font.AllBytes, x.Run.Text)).ToList();
            var book = FontCodebook.TryBuild(first, shown, learn, out reason);
            if (book is null) return null;

            // 되읽기 확인: 코드북으로 푼 글이 PDFium이 읽은 글과 다르면(쪽·글꼴이 어긋남) 쓰지 않는다.
            foreach (var (run, f) in infos)
            {
                string decoded = book.Decode(f.AllBytes);
                if (!SameIgnoringSpace(decoded, run.Text)) { reason = "decode-mismatch"; return null; }
            }
            tc = infos[0].Font.Tc;
            tz = infos[0].Font.Tz;
            return book;
        }
        catch (Exception e) when (e is IOException or PdfReaderException or InvalidOperationException or NotSupportedException)
        {
            reason = "pdfsharp-" + e.GetType().Name;
            return null;
        }
    }

    private static bool SameDict(PdfSharp.Pdf.PdfDictionary a, PdfSharp.Pdf.PdfDictionary b) =>
        ReferenceEquals(a, b) || (a.Reference is { } ra && b.Reference is { } rb && ra.ObjectNumber == rb.ObjectNumber);

    private static bool SameIgnoringSpace(string a, string b) =>
        string.Concat(a.Where(c => !char.IsWhiteSpace(c))) == string.Concat(b.Where(c => !char.IsWhiteSpace(c)));
}

/// <summary>
/// 문단 편집 계획(M5 C5·C9·C10·C14): 편집 창 글 → <see cref="ReplaceParagraphEdit"/>. PDFium 전용 스레드에서 부른다.
/// ① 원래 글꼴(코드북 + V14 규칙을 줄마다 통과)로 다시 쓰는 줄 전체 ② 하나라도 안 되면 다시 쓰는 줄 전체를 앱 글꼴로(C10 2패스) ③ 안 되면 거절 이유.
/// 바뀐 것이 없으면 편집을 만들지 않는다. 암호 문서는 거절.
/// </summary>
public static class ParagraphEditPlanner
{
    /// <param name="p">추정된(또는 이전 편집에 저장된) 원본 문단 모델.</param>
    /// <param name="previous">이 문단을 이미 고친 편집이 있으면 그것(대체됨).</param>
    /// <param name="korean">한글 줄 이음 방식(문서·열 단위 판정은 <see cref="DecideKorean"/>).</param>
    public static ParagraphPlan Plan(DocumentSession s, PageId page, ParagraphModel p, string newText, AppFont? appFont,
        ReplaceParagraphEdit? previous = null, KoreanLineBreak korean = KoreanLineBreak.Word, IParagraphFontSource? fontSource = null)
    {
        s.Thread.AssertOnThread();
        if (!SavePolicy.CanPatch(s)) return ParagraphPlan.Refused(ParagraphRefusal.Encrypted);
        int pageIndex = s.IndexOf(page);
        if (pageIndex < 0 || p.Lines.IsEmpty || !p.IsParagraph) return ParagraphPlan.Refused(ParagraphRefusal.NotParagraph);

        var r = s.GetReplay(page);
        var loaded = s.GetLoadedPage(page);
        var sz = loaded.SizePt;
        bool swap = loaded.Rotation % 2 != 0;
        var bounds = new PdfRectF(0, 0, swap ? sz.Height : sz.Width, swap ? sz.Width : sz.Height);

        var book = (fontSource ?? new SessionFileFontSource()).Build(s, page, p, out double tc, out double tz, out _);
        if (book is not null) p = WithTextState(p, (float)tc, (float)tz);
        var opt = new LayoutOptions { Korean = korean, PageBounds = bounds };
        var origMeasure = book is null ? null : new CodebookMeasure(book);
        var appMeasure = appFont is null ? null : new AppFontMeasure(appFont);
        if (origMeasure is null && appMeasure is null) return ParagraphPlan.Refused(ParagraphRefusal.NoAppFont);

        var layout = ParagraphLayout.Layout(p, newText, origMeasure, appMeasure, opt);
        if (layout.NoChange) return new ParagraphPlan(null, previous, layout, true, false, false, false, ParagraphRefusal.None, []);
        if (layout.Overflow) return ParagraphPlan.Refused(ParagraphRefusal.Overflow, null, layout);

        // 원래 글꼴로 정해졌다면 줄마다 V14 규칙을 통과해야 한다. 하나라도 못 하면 다시 쓰는 줄 전체를 앱 글꼴로 다시 배치한다(C10).
        if (!layout.DeleteAll && layout.Choice == FontChoice.Original)
        {
            if (book is null || !VerifyRewritten(s, pageIndex, p, layout, book))
            {
                if (appMeasure is null) return ParagraphPlan.Refused(ParagraphRefusal.NoAppFont, null, layout);
                layout = ParagraphLayout.Layout(p, newText, null, appMeasure, opt);
                if (layout.Overflow) return ParagraphPlan.Refused(ParagraphRefusal.Overflow, null, layout);
            }
        }
        if (!layout.MissingCodepoints.IsDefaultOrEmpty)
            return ParagraphPlan.Refused(appMeasure is null ? ParagraphRefusal.NoAppFont : ParagraphRefusal.MissingGlyphs, layout.MissingCodepoints, layout);

        var st = s.Content;
        int n = p.Lines.Length;

        // 끄기 대상: 다시 쓰는 줄이 대체하는 원본 줄(맞춰 동기화되면 first..last, 아니면 first..끝)
        var remove = ImmutableArray.CreateBuilder<ObjectRef>();
        int from, to;
        if (layout.DeleteAll) { from = 0; to = n - 1; }
        else
        {
            from = layout.FirstRewritten;
            to = layout.Lines.Length > layout.LastRewritten + 1 ? layout.LastRewritten : n - 1;
        }
        for (int i = from; i <= to && i < n; i++)
            foreach (var rf in p.Lines[i].AllRefs) remove.Add(rf);

        var origLines = ImmutableArray.CreateBuilder<ParagraphTextLine>();
        var appLines = ImmutableArray.CreateBuilder<AppTextSpec>();
        var appIds = ImmutableArray.CreateBuilder<long>();
        bool usedApp = layout.Choice == FontChoice.App && !layout.DeleteAll;
        var missing = new List<int>();
        if (!layout.DeleteAll)
        {
            for (int i = layout.FirstRewritten; i <= layout.LastRewritten && i < layout.Lines.Length; i++)
            {
                var line = layout.Lines[i];
                if (line.IsOriginal) continue;
                var srcRun = p.Lines[Math.Min(i, n - 1)].Runs[0];
                if (r.Resolve(srcRun.Ref) is not { Type: PdfPageObjectType.Text } srcObj) return ParagraphPlan.Refused(ParagraphRefusal.NotParagraph);
                var m = srcObj.Matrix;
                var placement = new PdfMatrix(m.A, m.B, m.C, m.D, line.BaselineOrigin.X, line.BaselineOrigin.Y);
                var color = p.Style.Color;
                if (layout.Choice == FontChoice.Original)
                {
                    byte[]? encoded = book is not null && book.TryEncode(line.Text, out var enc) ? enc.Bytes : null;
                    origLines.Add(new ParagraphTextLine(st.NewId(), line.Text, placement, srcObj.FontSize, color, srcRun.Ref,
                        srcRun.Tc, p.Style.Tz, line.JustifyExtra, srcObj.TextRenderMode, encoded, book?.TwoByte ?? false));
                }
                else
                {
                    var (spec, miss) = AppFontLine.Build(appFont!, line.Text, srcObj.FontSize, placement, color);
                    if (spec is null) { missing.AddRange(miss); continue; }
                    appLines.Add(spec);
                    appIds.Add(st.NewId());
                }
            }
            if (missing.Count > 0) return ParagraphPlan.Refused(ParagraphRefusal.MissingGlyphs, missing, layout);
        }

        var meta = new ParagraphMeta(p.Box, ParagraphLayout.OriginalText(p, opt), newText, p.Align.ToString(), p.Style.FontKey, p.Leading, p);
        var edit = new ReplaceParagraphEdit(st.NewId(), remove.ToImmutable(), origLines.ToImmutable(), appLines.ToImmutable(), appIds.ToImmutable(), meta);
        bool mixed = usedApp && layout.Lines.Any(l => l.IsOriginal);
        return new ParagraphPlan(edit, previous, layout, false, usedApp, mixed, layout.Overlap, ParagraphRefusal.None, []);
    }

    /// <summary>이전 편집(<see cref="ReplaceParagraphEdit"/>)에 저장된 원본 문단으로 다시 편집한다(원본 재추정 없음, C14). 모델이 없으면 거절.</summary>
    public static ParagraphPlan PlanReEdit(DocumentSession s, PageId page, ReplaceParagraphEdit previous, string newText, AppFont? appFont,
        KoreanLineBreak korean = KoreanLineBreak.Word, IParagraphFontSource? fontSource = null) =>
        previous.Meta.Model is ParagraphModel model
            ? Plan(s, page, model, newText, appFont, previous, korean, fontSource)
            : ParagraphPlan.Refused(ParagraphRefusal.NotParagraph);

    /// <summary>다시 쓰는 줄마다 V14 규칙(OriginalFontReplacer)을 통과하는지. 허용 글자 = 코드북이 아는 글자(문단에서 보인 글자).</summary>
    private static bool VerifyRewritten(DocumentSession s, int pageIndex, ParagraphModel p, ParagraphLayoutResult layout, FontCodebook book)
    {
        var allowed = book.Available.ToHashSet();
        int n = p.Lines.Length;
        for (int i = layout.FirstRewritten; i <= layout.LastRewritten && i < layout.Lines.Length; i++)
        {
            var line = layout.Lines[i];
            if (line.IsOriginal || line.Text.Length == 0) continue;
            var src = p.Lines[Math.Min(i, n - 1)].Runs[0].Ref;
            if (src.IsInserted) return false;
            if (OriginalFontReplacer.Verify(s, pageIndex, src.OriginalIndex, line.Text, allowed) != OriginalFontReplacer.Result.Ok) return false;
        }
        return true;
    }

    /// <summary>내용 스트림에서 읽은 자간·가로 비율이 클릭 경로의 기본값(0·100 %)과 다르면 모델의 폭 계산 재료를 바로잡는다.</summary>
    private static ParagraphModel WithTextState(ParagraphModel p, float tc, float tz)
    {
        if (Math.Abs(p.Style.Tz - tz) < 1e-3 && p.Lines.All(l => l.Runs.All(r => Math.Abs(r.Tc - tc) < 1e-4))) return p;
        TextLine Fix(TextLine l) => new()
        {
            Runs = [.. l.Runs.Select(r => r with { Tc = tc, Tz = tz })],
            Duplicates = l.Duplicates, Bounds = l.Bounds, Angle = l.Angle, U0 = l.U0, U1 = l.U1, Baseline = l.Baseline, Size = l.Size,
            FontKey = l.FontKey, Color = l.Color, Tz = tz, Mixed = l.Mixed, Text = l.Text, TableRow = l.TableRow,
        };
        return p with { Lines = [.. p.Lines.Select(Fix)], Style = p.Style with { Tz = tz } };
    }

    /// <summary>
    /// 한글 줄 이음 방식(V19, C16): 쪽 후보 줄에서 두 줄 이상 문단들의 비마지막 줄이 6개 이상일 때, 같은 문단의 가장 먼 줄 끝과의 여유가 1.1 em을 넘는 줄 비율이
    /// 30 % 이상이면 어절 단위, 15 % 이하면 글자 단위, 그 사이·표본 부족이면 어절 단위(공백 이음, 검증 필요). 한글이 없으면 어절 단위.
    /// </summary>
    public static KoreanLineBreak DecideKorean(IReadOnlyList<TextLine> lines, IReadOnlyList<Obstacle> obstacles, ParagraphOptions? options = null)
    {
        int total = 0, slack = 0;
        foreach (var para in ParagraphDetector.Partition(lines, obstacles, options))
        {
            if (!para.IsParagraph || !para.Lines.Any(l => l.Text.Any(c => c is >= '가' and <= '힯'))) continue;
            double right = para.Lines.Max(l => l.U1);
            for (int i = 0; i < para.Lines.Length - 1; i++)
            {
                var l = para.Lines[i];
                total++;
                if ((right - l.U1) / Math.Max(1e-3, l.Size) > 1.1) slack++;
            }
        }
        if (total < 6) return KoreanLineBreak.Word;
        double frac = (double)slack / total;
        return frac <= 0.15 ? KoreanLineBreak.Character : KoreanLineBreak.Word;
    }
}
