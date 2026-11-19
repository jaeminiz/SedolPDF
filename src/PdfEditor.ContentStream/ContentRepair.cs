namespace PdfEditor.ContentStream;

/// <summary>
/// 쪽 내용의 구조 고치기. 짝 없는 Q(저장한 그래픽 상태가 없는데 복원)는 PDFium·PDF-XChange가 무시하지만 Acrobat은 오류로 보고 쪽의 나머지를 그리지 않는다.
/// 0.8.0까지의 저장(PDFium이 여러 스트림 중 편집한 스트림만 다시 씀)이 이런 파일을 만들었다(2026-11-10 사용자 PDF) — 그런 파일을 다시 저장할 때 고친다.
/// 쪽 전체 규칙(<see cref="RemoveUnmatchedRestores"/>)은 무시되던 연산자만 빼지만, 스트림별 규칙(<see cref="RepairStreams"/>)은 뒤집힘 같은 모양도 바로잡는다.
/// </summary>
public static class ContentRepair
{
    /// <summary>짝 없는 Q 연산자를 지운 내용과 지운 수. 고칠 것이 없거나 해석할 수 없으면 (null, 0). 무시되던 연산자만 빼므로 관대한 뷰어의 모양은 같다.</summary>
    public static (byte[]? Data, int Removed) RemoveUnmatchedRestores(byte[] data)
    {
        var pc = ContentLexer.Parse(data);
        if (!pc.Ok) return (null, 0);
        return RemoveUnmatchedRestores(data, pc.Ops);
    }

    /// <summary>이미 해석한 연산자 목록(<paramref name="ops"/>는 <paramref name="data"/>를 해석한 것이고 오류 없음)으로 같은 일을 한다. 0.14.0: 해석을 되풀이하지 않는다.</summary>
    internal static (byte[]? Data, int Removed) RemoveUnmatchedRestores(byte[] data, List<ContentOp> ops)
    {
        var drop = FindUnmatchedDrops(ops);
        if (drop.Count == 0) return (null, 0);
        return (Cut(data, drop), drop.Count);
    }

    private static List<(int Start, int End)> FindUnmatchedDrops(List<ContentOp> ops)
    {
        var drop = new List<(int Start, int End)>();
        int depth = 0;
        foreach (var op in ops)
        {
            if (op.Name == "q") depth++;
            else if (op.Name == "Q")
            {
                if (depth == 0) drop.Add((op.Start, op.End));
                else depth--;
            }
        }
        return drop;
    }

    /// <summary>범위마다 지우고 그 자리에 공백 하나(앞뒤 토큰이 붙지 않게).</summary>
    private static byte[] Cut(byte[] data, List<(int Start, int End)> drop)
    {
        using var ms = new MemoryStream(data.Length);
        int at = 0;
        foreach (var (start, end) in drop)
        {
            ms.Write(data, at, start - at);
            ms.WriteByte((byte)' ');
            at = end;
        }
        ms.Write(data, at, data.Length - at);
        return ms.ToArray();
    }

    /// <summary>
    /// 스트림이 여럿인 쪽의 꼬리 조각까지 고친다(spec 3.2). <paramref name="streams"/>는 /Contents의 스트림을 풀어 따로 준 것이다.
    /// 감싸기(`q`만 있는 스트림)가 앞서 연 `q`를 가리고 있어 쪽 전체 규칙이 놓치던 `S Q` 꼬리를 지운다.
    /// 지우면 0.9.0 규칙과 달리 PDFium·PDF-XChange의 모양도 바뀐다(꺼지지 않아야 할 `cm`이 살아나 뒤집힘이 고쳐진다).
    /// 스트림 사이에는 <see cref="PageContent.Load"/>처럼 줄바꿈을 넣어 잇는다. 고칠 것이 없으면 Data는 null.
    /// 어느 스트림이든 해석할 수 없으면 쪽 전체 규칙만 쓴다.
    /// </summary>
    public static (byte[]? Data, int FragmentsRemoved, int RestoresRemoved) RepairStreams(IReadOnlyList<byte[]> streams)
    {
        var parsed = new List<ParsedContent>(streams.Count);
        foreach (var s in streams)
        {
            var pc = ContentLexer.Parse(s);
            if (!pc.Ok)
            {
                var (d, n) = RemoveUnmatchedRestores(Join(streams));
                return (d, 0, n);
            }
            parsed.Add(pc);
        }

        int fragments = 0;
        var stack = new Stack<bool>();   // 열린 q마다: 그림 스트림에서 열렸는가(true) / 감싸기 스트림에서 열렸는가(false)
        var cleaned = new List<byte[]>(streams.Count);
        var dropEnds = new int[streams.Count];   // 스트림마다 지운 꼬리 조각의 끝(없으면 -1)
        for (int k = 0; k < streams.Count; k++)
        {
            var ops = parsed[k].Ops;
            bool wrapper = ops.Count > 0 && ops.All(o => o.Name == "q");
            int dropEnd = -1;
            for (int i = 0; i < ops.Count; i++)
            {
                var op = ops[i];
                if (op.Name == "q") { stack.Push(!wrapper); continue; }
                if (op.Name != "Q") continue;
                // 머리(첫 q/Q 앞)에서 처음 만난 Q만 꼬리 조각인지 본다. 스택이 비었으면 앞을 지우면 그린 것까지 지우므로 쪽 전체 규칙에 맡긴다.
                bool firstStackOp = !ops.Take(i).Any(o => o.Name is "q" or "Q");
                if (firstStackOp && i > 0 && stack.Count > 0 && !stack.Peek())
                {
                    dropEnd = op.End; fragments++;   // 꺼지지 않은 것으로 친다: 이 Q는 실행되지 않으므로 스택은 그대로
                    continue;
                }
                if (stack.Count > 0) stack.Pop();
            }
            dropEnds[k] = dropEnd;
            if (dropEnd < 0) { cleaned.Add(streams[k]); continue; }
            var s = streams[k];
            var fixedStream = new byte[s.Length - dropEnd + 1];
            fixedStream[0] = (byte)' ';
            Array.Copy(s, dropEnd, fixedStream, 1, s.Length - dropEnd);
            cleaned.Add(fixedStream);
        }

        // 스트림이 이미 하나로 합쳐진 파일(0.9.0 이하의 고치기가 합쳤다)은 경계가 없어 위 규칙이 못 본다 — 꼬리 조각의 모양으로 찾는다.
        // 0.14.0: 스트림별로 해석한 결과를 이어(위치만 옮겨) 합친 내용의 연산자 목록을 만든다. 합친 내용을 다시 해석하던 두 번이 없어진다.
        // 스트림 끝에 키워드 없는 피연산자가 남았거나 인라인 이미지(BI)가 있으면 합쳐 해석한 결과와 달라질 수 있어 예전처럼 합쳐서 해석한다.
        var mergedOps = TryMergeOps(streams, parsed, cleaned, dropEnds);
        byte[]? joined = null;
        List<ContentOp> opsOfJoined;
        if (mergedOps is not null) opsOfJoined = mergedOps;
        else { joined = Join(cleaned); var pj = ContentLexer.Parse(joined); opsOfJoined = pj.Ok ? pj.Ops : null!; }

        List<(int Start, int End)> dangling = opsOfJoined is null ? [] : FindDanglingDrops(opsOfJoined);
        fragments += dangling.Count;
        byte[]? noFragments = null;
        int restores;
        byte[]? data;
        if (dangling.Count == 0)
        {
            if (opsOfJoined is null) { joined ??= Join(cleaned); (data, restores) = (null, 0); }
            else
            {
                // 고칠 것이 없는 쪽(거의 전부)은 합친 바이트도 만들지 않는다.
                var drop = FindUnmatchedDrops(opsOfJoined);
                restores = drop.Count;
                data = null;
                if (restores > 0) { joined ??= Join(cleaned); data = Cut(joined, drop); }
            }
            if (fragments == 0 && restores == 0) return (null, 0, 0);
            joined ??= Join(cleaned);
            return (data ?? joined, fragments, restores);
        }
        joined ??= Join(cleaned);
        noFragments = Cut(joined, dangling);
        (data, restores) = RemoveUnmatchedRestores(noFragments);   // 바뀐 내용은 새로 해석한다(꼬리 조각이 있는 드문 쪽만)
        return (data ?? noFragments, fragments, restores);
    }

    /// <summary>스트림별 해석 결과로 <c>Join(cleaned)</c>를 해석한 것과 같은 연산자 목록을 만든다. 같게 만들 수 없으면 null.</summary>
    private static List<ContentOp>? TryMergeOps(IReadOnlyList<byte[]> streams, List<ParsedContent> parsed, List<byte[]> cleaned, int[] dropEnds)
    {
        var all = new List<ContentOp>();
        int offset = 0;
        for (int k = 0; k < streams.Count; k++)
        {
            var ops = parsed[k].Ops;
            if (ops.Any(o => o.Name == "BI") || HasTrailingOperands(streams[k], ops)) return null;
            int dropEnd = dropEnds[k];
            int shift = dropEnd < 0 ? offset : offset + 1 - dropEnd;
            foreach (var o in ops)
            {
                if (dropEnd >= 0 && o.Start < dropEnd) continue;   // 지워진 꼬리 조각(연산자는 순서대로라 앞쪽만 해당)
                all.Add(new ContentOp { Name = o.Name, Start = o.Start + shift, OpStart = o.OpStart + shift, End = o.End + shift, Operands = o.Operands });
            }
            offset += cleaned[k].Length + 1;
        }
        return all;
    }

    /// <summary>마지막 연산자 뒤에 공백·주석 말고 남은 것(키워드 없이 끝난 피연산자)이 있는가.</summary>
    private static bool HasTrailingOperands(byte[] d, List<ContentOp> ops)
    {
        int p = ops.Count == 0 ? 0 : ops[^1].End;
        while (p < d.Length)
        {
            byte b = d[p];
            if (b is 0 or 9 or 10 or 12 or 13 or 32) p++;
            else if (b == '%') { while (p < d.Length && d[p] != 10 && d[p] != 13) p++; }
            else return true;
        }
        return false;
    }

    /// <summary>
    /// <see cref="RepairStreams"/>와 같은 결과를 내되, 같은 내용(스트림 경계 포함)이 이미 깨끗하다고 확인돼 있으면 해석하지 않는다(<see cref="ContentRepairCache"/>).
    /// 깨끗한 결과(고칠 것 없음)는 캐시에 넣고, 고칠 것이 있는 쪽은 넣지 않는다. 스레드 안전.
    /// </summary>
    public static (byte[]? Data, int FragmentsRemoved, int RestoresRemoved) RepairStreamsCached(IReadOnlyList<byte[]> streams)
    {
        var key = ContentRepairCache.Fingerprint(streams);
        if (ContentRepairCache.IsClean(key)) return (null, 0, 0);
        var r = RepairStreams(streams);
        if (r.Data is null) ContentRepairCache.MarkClean(key);
        return r;
    }

    private static readonly HashSet<string> PaintOps = ["S", "s", "f", "F", "f*", "B", "B*", "b", "b*"];

    /// <summary>
    /// 이미 합쳐진 내용에서 꼬리 조각을 찾는다: 현재 점이 없는데(길 연산자 m·re·l …이 하나도 없음) 칠하기(S f B …)가 나오고, 칠하기만 이어지다가 곧바로 `Q`가 오는 모양(4쪽의 `S Q`).
    /// 처음부터 그렇게 쓴 파일은 없다(현재 점 없는 칠하기는 규격 오류) — 앞 스트림을 다시 쓸 때 이미 통째로 그려진 길의 남은 꼬리와 그것이 꺼 버리는 `Q`다. 그 둘을 지운다. 길 연산자가 딸린 조각(`l l S Q`)은 건드리지 않는다(실제 파일 6쪽: 다시 쓴 스트림이 길 한가운데에 끼어 있다).
    /// </summary>
    public static (byte[]? Data, int Removed) RemoveDanglingFragments(byte[] data)
    {
        var pc = ContentLexer.Parse(data);
        if (!pc.Ok) return (null, 0);
        var drop = FindDanglingDrops(pc.Ops);
        if (drop.Count == 0) return (null, 0);
        return (Cut(data, drop), drop.Count);
    }

    private static List<(int Start, int End)> FindDanglingDrops(List<ContentOp> ops)
    {
        var drop = new List<(int Start, int End)>();
        bool havePoint = false;
        for (int i = 0; i < ops.Count; i++)
        {
            string n = ops[i].Name;
            if (n is "m" or "re" or "l" or "c" or "v" or "y" or "h") { havePoint = true; continue; }   // 길 연산자가 하나라도 있었으면 칠하기는 그 길을 그린다(m 없이 이어진 l도 센다)
            if (n == "n") { havePoint = false; continue; }   // W 뒤의 끝맺음
            bool isPaint = PaintOps.Contains(n);
            if (!isPaint) continue;
            if (havePoint) { havePoint = false; continue; }   // 정상: 칠하면 현재 점이 사라진다
            // 현재 점 없는 칠하기: 칠하기만 이어지다가 곧바로 Q인가
            int j = i;
            while (j < ops.Count && PaintOps.Contains(ops[j].Name)) j++;
            if (j < ops.Count && ops[j].Name == "Q")
            {
                drop.Add((ops[i].Start, ops[j].End));
                i = j;
                havePoint = false;
            }
        }
        return drop;
    }

    private static byte[] Join(IReadOnlyList<byte[]> streams)
    {
        using var ms = new MemoryStream();
        foreach (var s in streams) { ms.Write(s); ms.WriteByte((byte)'\n'); }
        return ms.ToArray();
    }
}
