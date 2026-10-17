using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Commands;
using PdfEditor.Document.Saving;

namespace PdfEditor.Exchange.Xfdf;

/// <summary>가져오기 계획: 실행할 명령(없으면 null), 가져올 주석 수, 건너뛴 이유, 참고 사항.</summary>
public sealed record XfdfImportPlan(IUndoableCommand? Command, int Imported, IReadOnlyList<string> Skipped, IReadOnlyList<string> Notes);

/// <summary>
/// 읽은 XFDF를 문서에 넣는 명령 하나로 만든다 (M3b Task 6, B6). 같은 /NM이 문서에 있으면 그 주석을 바꾸고(Replace), 없으면 새로 만든다.
/// 전체가 <see cref="CompositeCommand"/> 하나라 실행 취소 한 번에 모두 돌아온다. 호출 전에 <c>await session.AnnotationsReady</c>.
/// </summary>
public static class XfdfImport
{
    /// <summary>답글 사슬 깊이 상한. 넘으면 그 답글부터 새 뿌리가 된다.</summary>
    public const int MaxThreadDepth = 256;

    public static IUndoableCommand? BuildCommand(DocumentSession s, XfdfReadResult r, out IReadOnlyList<string> skipped)
    {
        var plan = Plan(s, r);
        skipped = plan.Skipped;
        return plan.Command;
    }

    private sealed class Entry
    {
        public (int PageIndex, Annotation A) Source;
        public int Page;                      // 들어갈 쪽(0부터)
        public Annotation? Existing;          // 바꿀 기존 주석
        public Annotation Final = null!;
        public bool Dropped;
        public string Element => Source.A.Kind.ToString().ToLowerInvariant();
    }

    public static XfdfImportPlan Plan(DocumentSession s, XfdfReadResult r)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(r);
        var skipped = new List<string>(r.Skipped);
        var notes = new List<string>(r.Notes);
        var pages = s.Pages;
        bool patchable = SavePolicy.CanPatch(s);

        // 문서의 기존 주석: NM → (쪽, 주석)
        var existingByName = new Dictionary<string, (int Page, Annotation A)>(StringComparer.Ordinal);
        var existingById = new Dictionary<AnnotId, (int Page, Annotation A)>();
        for (int i = 0; i < pages.Length; i++)
            foreach (var v in s.Annotations.Get(pages[i].Id).Items)
            {
                existingById[v.Data.Id] = (i, v.Data);
                if (!string.IsNullOrEmpty(v.Data.Name)) existingByName.TryAdd(v.Data.Name, (i, v.Data));
            }

        // 1단계: 걸러내고 바꿀 대상을 정한다.
        var entries = new List<Entry>(r.Annotations.Count);
        foreach (var src in r.Annotations)
        {
            var e = new Entry { Source = src, Page = src.PageIndex };
            string el = e.Element;
            if (src.PageIndex >= pages.Length)
            {
                skipped.Add($"{el}: page {src.PageIndex + 1} does not exist in this document ({pages.Length} pages)");
                continue;
            }
            if (!patchable && !IsM2Kind(src.A))
            {
                skipped.Add($"{el}: not supported in an encrypted document");
                continue;
            }
            if (src.A.Name is { } nm && existingByName.TryGetValue(nm, out var ex))
            {
                e.Existing = ex.A;
                if (ex.Page != src.PageIndex)
                {
                    notes.Add($"{el}: the existing annotation '{nm}' is on page {ex.Page + 1}; it was replaced there");
                    e.Page = ex.Page;
                }
            }
            entries.Add(e);
        }

        // 2단계: 답글 연결. 최종 Id는 바꿀 대상이면 기존 Id, 아니면 파일에서 받은 Id.
        var bySourceId = entries.ToDictionary(e => e.Source.A.Id);
        AnnotId FinalId(Entry e) => e.Existing?.Id ?? e.Source.A.Id;

        // 사슬이 맴돌거나 너무 깊으면 끊는다. 항목마다 한 번씩만 지나므로 전체가 선형이다.
        var cut = new HashSet<Entry>();
        var finished = new Dictionary<Entry, int>();   // 항목 → 깊이
        foreach (var start in entries)
        {
            if (finished.ContainsKey(start)) continue;
            var path = new List<Entry>();
            var onPath = new HashSet<Entry>();
            Entry? cur = start;
            bool cyc = false;
            while (cur is not null && !finished.ContainsKey(cur))
            {
                if (!onPath.Add(cur)) { cyc = true; break; }
                path.Add(cur);
                cur = cur.Source.A.InReplyTo is { } pid && bySourceId.TryGetValue(pid, out var nx) ? nx : null;
            }
            if (cyc)
            {
                foreach (var n in path) { cut.Add(n); finished[n] = 0; }
                continue;
            }
            int parentDepth = cur is null ? -1 : finished[cur];
            for (int i = path.Count - 1; i >= 0; i--)
            {
                int d = parentDepth + 1;
                if (d > MaxThreadDepth) { cut.Add(path[i]); d = 0; }
                finished[path[i]] = d;
                parentDepth = d;
            }
        }

        foreach (var e in entries)
        {
            var a = e.Source.A;
            bool wasReply = a.InReplyTo is not null || r.ParentNames.ContainsKey(a.Id);
            AnnotId? parentFinal = null;
            int? parentPage = null;
            Annotation? parentData = null;
            string? why = null;

            if (a.InReplyTo is { } pid)
            {
                if (bySourceId.TryGetValue(pid, out var pe) && !cut.Contains(e))
                {
                    parentFinal = FinalId(pe);
                    parentPage = pe.Page;
                    parentData = pe.Source.A;
                }
                else why = pe is null ? "its parent was not imported" : "its parent chain is circular or too deep";
            }
            else if (r.ParentNames.TryGetValue(a.Id, out var pn))
            {
                if (existingByName.TryGetValue(pn, out var ex))
                {
                    parentFinal = ex.A.Id;
                    parentPage = ex.Page;
                    parentData = ex.A;
                }
                else why = $"its parent '{pn}' is not in the document";
            }

            if (parentFinal is not null && a.ReplyKind == ReplyType.Group && parentData?.Kind != AnnotKind.StrikeOut)
            {
                parentFinal = null;
                why = "a replace caret needs a strikeout parent";
            }

            if (parentFinal is { } pf)
            {
                if (parentPage is int pp && pp != e.Page)
                {
                    notes.Add($"{e.Element}: moved to page {pp + 1} next to its parent");
                    e.Page = pp;
                }
                e.Final = a with { InReplyTo = pf };
            }
            else
            {
                if (a.State is not null)
                {
                    e.Dropped = true;
                    skipped.Add($"{e.Element}: a review state needs a parent annotation ({why ?? "no parent"})");
                    continue;
                }
                if (wasReply) notes.Add($"{e.Element}: imported as a normal annotation because {why ?? "its parent is missing"}");
                e.Final = a with { InReplyTo = null, ReplyKind = null };
            }
            if (e.Existing is { } old) e.Final = e.Final with { Id = old.Id };
            if (e.Existing is { } old2 && old2.Kind != a.Kind) notes.Add($"{e.Element}: replaced annotation of a different kind ({old2.Kind.ToString().ToLowerInvariant()}) with the same name");
        }

        // 부모를 못 넣은 상태 때문에 버려진 것의 답글이 남았다면 부모가 없는 것으로 다시 다룬다: 드문 경우라 여기서는 연결만 확인한다.
        var droppedIds = entries.Where(e => e.Dropped).Select(e => e.Source.A.Id).ToHashSet();
        if (droppedIds.Count > 0)
            foreach (var e in entries.Where(e => !e.Dropped && e.Final.InReplyTo is { } p && droppedIds.Contains(p)))
                e.Final = e.Final with { InReplyTo = null, ReplyKind = null };

        // 첨부 파일은 가져오기로 확정된 주석만 저장소에 넣는다.
        foreach (var e in entries.Where(e => !e.Dropped && e.Final.Kind == AnnotKind.FileAttachment))
        {
            var stored = r.StoreAttachment(e.Final, out var serr);
            if (stored is null) { e.Dropped = true; skipped.Add($"{e.Element}: {serr}"); }
            else e.Final = stored;
        }

        // 3단계: 쪽별로 명령 묶기. 부모가 먼저 오도록 답글은 뒤로.
        var parts = new List<IUndoableCommand>();
        int imported = 0;
        foreach (var g in entries.Where(e => !e.Dropped).GroupBy(e => e.Page).OrderBy(g => g.Key))
        {
            var changes = new List<AnnotationChange>();
            foreach (var e in g.OrderBy(e => e.Final.InReplyTo is null ? 0 : 1))
            {
                changes.Add(new AnnotationChange(e.Existing, e.Final));
                imported++;
            }
            parts.Add(new ChangeAnnotationsCommand(pages[g.Key].Id, changes));
        }
        var cmd = parts.Count == 0 ? null : new CompositeCommand(parts);
        return new XfdfImportPlan(cmd, imported, skipped, notes);
    }

    /// <summary>암호 문서(패치 불가)에서 가져올 수 있는 M2 유형: 펜·형광펜·밑줄·취소선·메모(답글 아님)·사각형(구름 아님)·원.</summary>
    private static bool IsM2Kind(Annotation a) => a.Kind switch
    {
        AnnotKind.Ink or AnnotKind.Highlight or AnnotKind.Underline or AnnotKind.StrikeOut or AnnotKind.Circle => true,
        AnnotKind.Text => a.InReplyTo is null && a.State is null && !HasParentName(a),
        AnnotKind.Square => a.CloudIntensity <= 0f,
        _ => false,
    };

    private static bool HasParentName(Annotation a) => a.ReplyKind is not null;
}
