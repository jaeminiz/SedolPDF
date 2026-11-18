using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.ContentStream;
using PdfEditor.Document;
using PdfEditor.Document.Saving;
using PdfEditor.Document.Content;
using PdfEditor.TextEdit.FindReplace;
using PdfSharp.Pdf.IO;

namespace PdfEditor.TextEdit;

/// <summary>
/// 한 줄 편집의 부분 바꾸기(0.13.0 2차): TJ 간격(표제란 칸 사이)이 있는 글 객체는 줄 전체를 다시 쓰면 간격이 사라지므로 <b>바뀐 글자만</b> 바꾼다.
/// 옛 글과 새 글의 공통 앞·뒷부분을 뺀 바뀐 구간이 가짜 공백(PDFium이 큰 TJ 이동을 흉내 낸 공백)을 건드리지 않을 때만 쓴다 — 아니면 null(줄 전체).
/// ① 글자 수가 같고 새 글자가 모두 원래 글꼴 코드북에 있으면 코드만 제자리 교체(<see cref="ReplaceTextSpanEdit.CodeSwaps"/>) ② 아니면 찾아 바꾸기의 낱말 하나 바꾸기(<see cref="ReplaceTextSpanEdit"/>).
/// 가짜 공백이 없는 글 객체는 다루지 않는다(기존 한 줄 편집이 그대로 맞다). PDFium 전용 스레드에서 부른다.
/// </summary>
public static class PartialReplacePlanner
{
    public static IReadOnlyList<ContentEdit>? TryPlan(DocumentSession s, PageId page, int originalIndex, string newText, AppFont? appFont, AppFontCatalog? catalog)
    {
        s.Thread.AssertOnThread();
        if (originalIndex < 0 || s.IsEncrypted || string.IsNullOrWhiteSpace(newText) || !SavePolicy.CanPatch(s)) return null;
        string? source = s.FontSourcePath is { } fsp && File.Exists(fsp) ? fsp : s.FilePath;
        if (source is null || !File.Exists(source)) return null;
        int pageIndex = s.IndexOf(page);
        if (pageIndex < 0) return null;
        try
        {
            var replay = s.GetReplay(page);
            if (originalIndex >= replay.Originals.Count || replay.Originals[originalIndex] is not { Type: PdfPageObjectType.Text } srcObj) return null;
            string? oldText;
            using (var pg = s.Document.LoadPage(pageIndex))
            {
                if (originalIndex >= pg.ObjectCount) return null;
                using var tp = pg.LoadText();
                oldText = pg.GetObject(originalIndex).GetText(tp);
            }
            if (oldText is null || oldText == newText) return null;

            using var doc = PdfReader.Open(source, PdfDocumentOpenMode.Import);
            if (pageIndex >= doc.PageCount) return null;
            var index = PageTextIndex.Build(doc.Pages[pageIndex], pageIndex);
            if (!index.Walk.Ok) return null;
            var seg = ContentSegmenter.Segment(index.Walk.Streams[0], PageContent.XObjectResolver(index.Page));
            if (!seg.Ok || seg.Objects.Count != replay.Originals.Count) return null;
            for (int i = 0; i < seg.Objects.Count; i++)
                if ((int)seg.Objects[i].Kind != (int)replay.Originals[i].Type) return null;
            if (seg.Objects[originalIndex].Kind != ObjectKind.Text) return null;
            int op = seg.Objects[originalIndex].OpIndex;

            var glyphs = index.Walk.Glyphs.Where(g => g.StreamId == 0 && g.Op == op).OrderBy(g => g.Str).ThenBy(g => g.Code).ToList();
            if (glyphs.Count == 0 || glyphs.Any(g => g.QuoteOp || g.Tr >= 4 || g.Font is null) || glyphs.Select(g => g.Font).Distinct().Count() != 1) return null;

            // 옛 글(PDFium)의 글자 번호 → 글자(가짜 공백이면 -1)
            var map = new List<int>(oldText.Length);
            int gi = 0;
            for (int p = 0; p < oldText.Length;)
            {
                if (gi < glyphs.Count && glyphs[gi].Text.Length > 0 && string.CompareOrdinal(oldText, p, glyphs[gi].Text, 0, glyphs[gi].Text.Length) == 0)
                {
                    for (int k = 0; k < glyphs[gi].Text.Length; k++) map.Add(gi);
                    p += glyphs[gi].Text.Length; gi++;
                }
                else if (char.IsWhiteSpace(oldText[p])) { map.Add(-1); p++; }
                else return null;
            }
            if (gi != glyphs.Count) return null;
            if (!map.Contains(-1)) return null;   // 가짜 공백이 없는 글 객체는 기존 한 줄 편집이 맞다

            // 끝 공백 무시: PDFium 글은 끝에 공백이 붙어 오고 편집 칸은 끝 공백을 잘라 주므로 둘 다 끝 공백을 뺀 채 비교한다(글자 번호 → 글자 대응은 앞쪽이라 그대로 맞다).
            oldText = oldText.TrimEnd();
            newText = newText.TrimEnd();
            if (oldText.Length == 0 || oldText == newText) return null;

            // 바뀐 구간(공통 앞·뒷부분 제외)
            int a = 0;
            while (a < oldText.Length && a < newText.Length && oldText[a] == newText[a]) a++;
            int b = 0;
            while (b < oldText.Length - a && b < newText.Length - a && oldText[^(b + 1)] == newText[^(b + 1)]) b++;
            if (a == oldText.Length - b)   // 끼워 넣기만: 앞 글자 하나를 같이 바꾼다
            {
                if (a > 0) a--; else if (b > 0) b--; else return null;
            }
            int oldEnd = oldText.Length - b, newEnd = newText.Length - b;
            if (oldEnd <= a || newEnd < a) return null;
            if (a == 0 && b == 0) return null;
            int g0 = map[a], g1 = map[oldEnd - 1];
            if (g0 < 0 || g1 < g0) return null;
            for (int k = a; k < oldEnd; k++) if (map[k] < 0) return null;   // 가짜 공백(칸 사이)을 건드림
            if (a > 0 && map[a - 1] == g0 || oldEnd < map.Count && map[oldEnd] == g1) return null;   // 여러 글자짜리 글자의 한가운데
            string newRange = newText[a..newEnd];
            if (newRange.Any(char.IsWhiteSpace)) return null;

            var font = glyphs[0].Font!;
            var range = glyphs.GetRange(g0, g1 - g0 + 1);

            // ① 같은 글자 수, 새 글자가 모두 코드북에 있음 → 코드만 바꾼다
            if (range.Count == newRange.Length && range.All(g => g.Text.Length == 1) && font.Book is { } book && !font.Type3)
            {
                var swaps = ImmutableArray.CreateBuilder<CodeSwapRef>();
                var newTexts = new Dictionary<GlyphInfo, string>();
                bool ok = true;
                for (int k = 0; k < range.Count && ok; k++)
                {
                    if (!book.TryEncode(newRange[k].ToString(), out var enc)) { ok = false; break; }
                    int code = book.TwoByte ? (enc.Bytes[0] << 8) | enc.Bytes[1] : enc.Bytes[0];
                    swaps.Add(new CodeSwapRef(range[k].Str, range[k].Code, code));
                    newTexts[range[k]] = newRange[k].ToString();
                }
                if (ok && OriginalFontReplacer.Verify(s, pageIndex, originalIndex, newRange, book.Available.ToHashSet()) == OriginalFontReplacer.Result.Ok)
                    return [BuildSwapEdit(s, srcObj, originalIndex, font.Unit, glyphs, map, newTexts, swaps.ToImmutable())];
            }

            // ② 바뀐 구간만 찾아 바꾸기 계획으로
            var match = new TextMatch(page, pageIndex, TextMatcher.SpansOf(range), range, oldText[a..oldEnd], "", "", TextMatcher.BoundsOf(range), range[0].Angle, -1) { Source = index };
            var plan = ReplacePlanner.Plan(match, newRange, null);
            if (!plan.Ok) return null;
            var batch = FindReplaceService.PlanOnThread(s, [plan], newRange, appFont, catalog);
            if (batch.Items.Count != 1 || !batch.Items[0].Ok) return null;
            // 바뀐 구간이 칸 끝(뒤가 칸 간격이나 줄 끝)이면 길이가 달라져도 뒤 칸을 밀지 않는다(간격은 그대로, 새 글이 간격 안으로 넘칠 뿐). 칸 안에 뒤따르는 글자가 있으면 기존 찾아 바꾸기처럼 같은 칸의 뒷글자를 밀되 뒤 칸도 같이 밀린다.
            bool atColumnEnd = oldEnd >= map.Count || map[oldEnd] < 0;
            if (atColumnEnd)
            {
                var it0 = batch.Items[0];
                batch = new ReplaceBatch { Items = [new ReplaceItem { Match = it0.Match, Plan = it0.Plan with { NewLengthPt = 0 }, Refusal = it0.Refusal, Font = it0.Font, Overlap = it0.Overlap, App = it0.App }], Objects = batch.Objects };
            }
            if (FindReplaceService.BuildCommand(s, batch) is not ContentEditBatchCommand cmd) return null;
            return [.. cmd.Edits.Select(e => e.Edit)];
        }
        catch (Exception e) when (e is IOException or PdfReaderException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>화면: 칸(가짜 공백) 단위로 나눠 PDFium 글 객체로 — 칸 사이 간격이 화면에서도 그대로 보인다.</summary>
    private static ReplaceTextSpanEdit BuildSwapEdit(DocumentSession s, PdfPageObject src, int objectIndex, int unit, List<GlyphInfo> glyphs, List<int> map,
        Dictionary<GlyphInfo, string> newTexts, ImmutableArray<CodeSwapRef> swaps)
    {
        var m0 = src.Matrix;
        var screen = ImmutableArray.CreateBuilder<ScreenTextPiece>();
        var run = new System.Text.StringBuilder();
        GlyphInfo? first = null;
        void Flush()
        {
            if (first is not null && run.ToString().Trim().Length > 0)
                screen.Add(new ScreenTextPiece(s.Content.NewId(), run.ToString(), new PdfMatrix(m0.A, m0.B, m0.C, m0.D, (float)first.Place.E, (float)first.Place.F)));
            run.Clear(); first = null;
        }
        // 가짜 공백으로 끊기는 글자 사이에서 칸을 나눈다
        var breaksAfter = new HashSet<int>();
        for (int i = 0; i < map.Count; i++)
            if (map[i] < 0 && i > 0 && map[i - 1] >= 0) breaksAfter.Add(map[i - 1]);
        for (int gi = 0; gi < glyphs.Count; gi++)
        {
            first ??= glyphs[gi];
            run.Append(newTexts.TryGetValue(glyphs[gi], out var nt) ? nt : glyphs[gi].Text);
            if (breaksAfter.Contains(gi)) Flush();
        }
        Flush();
        var b = TextMatcher.BoundsOf(glyphs);
        var mask = new PdfRectF((float)(b.L - 2), (float)(b.B - 2), (float)(b.R + 2), (float)(b.T + 2));
        return new ReplaceTextSpanEdit(s.Content.NewId(), ObjectRef.Original(objectIndex), unit, [], screen.ToImmutable(), [], [], mask, swaps);
    }
}
