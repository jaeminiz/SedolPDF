using Pdf.Native;

namespace PdfEditor.Document.Content;

/// <summary>화면용(앱 글꼴 줄을 윤곽선 경로로) 또는 출력용(앱 글꼴 줄은 건너뜀 — 패치 단계가 진짜 글자로 씀).</summary>
public enum ReplayMode { Screen, Output }

/// <summary>replay 뒤 객체를 찾는 표. Originals[i] = 원본 i번 객체, Inserted[id] = 편집 id가 넣은 객체.</summary>
public sealed class ReplayResult(IReadOnlyList<PdfPageObject> originals, IReadOnlyDictionary<long, PdfPageObject> inserted, int skipped)
{
    public IReadOnlyList<PdfPageObject> Originals { get; } = originals;
    public IReadOnlyDictionary<long, PdfPageObject> Inserted { get; } = inserted;

    /// <summary>대상을 찾지 못해 건너뛴 편집 수(정상이면 0).</summary>
    public int Skipped { get; } = skipped;

    public PdfPageObject? Resolve(ObjectRef r) =>
        r.IsInserted ? Inserted.GetValueOrDefault(r.InsertedId)
        : r.OriginalIndex >= 0 && r.OriginalIndex < Originals.Count ? Originals[r.OriginalIndex] : null;
}

/// <summary>
/// 편집 기록을 갓 불러온 쪽에 적용한다(PDFium 전용 스레드). 원본 번호를 먼저 모두 핸들로 잡은 뒤 적용하므로
/// 넣기로 번호가 밀려도 대상이 바뀌지 않는다(C2). GenerateContent는 부르지 않는다(호출자 몫, 출력 사본에서만).
/// </summary>
public static class ContentReplayer
{
    public static ReplayResult Apply(PdfPage page, IReadOnlyList<ContentEdit> edits, ReplayMode mode = ReplayMode.Screen)
    {
        int n = page.ObjectCount;
        var originals = new PdfPageObject[n];
        for (int i = 0; i < n; i++) originals[i] = page.GetObject(i);
        var inserted = new Dictionary<long, PdfPageObject>();
        if (edits.Count == 0) return new ReplayResult(originals, inserted, 0);

        // 원본 i번 바로 뒤에 넣은 객체 수(위치 계산용)
        var afterCounts = new SortedList<int, int>();
        int InsertPosAfter(int orig)
        {
            int extra = 0;
            foreach (var (k, c) in afterCounts) { if (k > orig) break; extra += c; }
            return orig + 1 + extra;
        }

        int skipped = 0;
        var result = new ReplayResult(originals, inserted, 0);
        // 앱 글꼴 줄(C7)은 화면에서는 다른 편집을 다 적용한 뒤 맨 위에 넣는다. 그 줄을 가리키는 편집(옮기기·끄기)은
        // 줄을 넣은 뒤에 적용해야 하므로 함께 미룬다. 출력 모드에서는 넣지 않는다(패치 단계가 진짜 글자로 씀).
        var appText = edits.OfType<InsertAppTextEdit>().ToDictionary(a => a.Id);
        bool TargetsAppText(ObjectRef r) => r.IsInserted && appText.ContainsKey(r.InsertedId);
        var deferred = new List<ContentEdit>();
        foreach (var e in edits)
        {
            if (e is InsertAppTextEdit || e is DeactivateEdit { Target: var dt } && TargetsAppText(dt) || e is TransformEdit { Target: var tt } && TargetsAppText(tt))
            {
                if (mode == ReplayMode.Screen) deferred.Add(e);
                continue;
            }
            switch (e)
            {
                case DeactivateEdit d:
                    if (result.Resolve(d.Target) is { } od) od.IsActive = false; else skipped++;
                    break;
                case TransformEdit t:
                    if (result.Resolve(t.Target) is { } ot) ot.TransformBy(t.Then); else skipped++;
                    break;
                case InsertTextLikeEdit it:
                    if (it.Original.IsInserted || result.Resolve(it.Original) is not { } src || page.CreateTextLike(src, it.Text) is not { } nt)
                    {
                        skipped++;
                        break;
                    }
                    int orig = it.Original.OriginalIndex;
                    page.InsertObject(nt, InsertPosAfter(orig));
                    afterCounts[orig] = afterCounts.GetValueOrDefault(orig) + 1;
                    inserted[it.Id] = nt;
                    break;
                default:
                    skipped++;
                    break;
            }
        }
        foreach (var e in deferred)
        {
            switch (e)
            {
                case InsertAppTextEdit a:
                    var path = BuildOutlinePath(page, a.Line);
                    if (path is null) { skipped++; break; }
                    page.InsertObject(path);
                    inserted[a.Id] = path;
                    break;
                case DeactivateEdit d:
                    if (result.Resolve(d.Target) is { } od) od.IsActive = false; else skipped++;
                    break;
                case TransformEdit t:
                    if (result.Resolve(t.Target) is { } ot) ot.TransformBy(t.Then); else skipped++;
                    break;
            }
        }
        return new ReplayResult(originals, inserted, skipped);
    }

    /// <summary>앱 글꼴 줄의 글리프 윤곽선을 채운 경로 객체 하나로 만든다(쪽에는 넣지 않음). 윤곽선이 없으면 null.</summary>
    private static PdfPageObject? BuildOutlinePath(PdfPage page, AppTextSpec line)
    {
        if (line.Outline.IsDefaultOrEmpty) return null;
        var first = line.Outline[0];
        var path = page.CreatePath(first.A.X, first.A.Y);
        foreach (var s in line.Outline)
        {
            switch (s.Op)
            {
                case 'M': path.MoveTo(s.A.X, s.A.Y); break;
                case 'L': path.LineTo(s.A.X, s.A.Y); break;
                case 'C': path.BezierTo(s.A.X, s.A.Y, s.B.X, s.B.Y, s.C.X, s.C.Y); break;
                case 'Z': path.ClosePath(); break;
            }
        }
        path.FillColor = new PdfRgba(line.Color.R, line.Color.G, line.Color.B, 255);
        path.SetDrawMode(fill: true, stroke: false);
        return path;
    }
}
