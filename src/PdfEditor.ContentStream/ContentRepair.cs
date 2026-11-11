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
        var drop = new List<ContentOp>();
        int depth = 0;
        foreach (var op in pc.Ops)
        {
            if (op.Name == "q") depth++;
            else if (op.Name == "Q")
            {
                if (depth == 0) drop.Add(op);
                else depth--;
            }
        }
        if (drop.Count == 0) return (null, 0);
        using var ms = new MemoryStream(data.Length);
        int at = 0;
        foreach (var op in drop)
        {
            ms.Write(data, at, op.Start - at);
            ms.WriteByte((byte)' ');   // 앞뒤 토큰이 붙지 않게
            at = op.End;
        }
        ms.Write(data, at, data.Length - at);
        return (ms.ToArray(), drop.Count);
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
            if (dropEnd < 0) { cleaned.Add(streams[k]); continue; }
            var s = streams[k];
            var fixedStream = new byte[s.Length - dropEnd + 1];
            fixedStream[0] = (byte)' ';
            Array.Copy(s, dropEnd, fixedStream, 1, s.Length - dropEnd);
            cleaned.Add(fixedStream);
        }

        var joined = Join(cleaned);
        // 스트림이 이미 하나로 합쳐진 파일(0.9.0 이하의 고치기가 합쳤다)은 경계가 없어 위 규칙이 못 본다 — 꼬리 조각의 모양으로 찾는다.
        var (noFragments, merged) = RemoveDanglingFragments(joined);
        fragments += merged;
        var (data, restores) = RemoveUnmatchedRestores(noFragments ?? joined);
        if (fragments == 0 && restores == 0) return (null, 0, 0);
        return (data ?? noFragments ?? joined, fragments, restores);
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
        var ops = pc.Ops;
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
        if (drop.Count == 0) return (null, 0);
        using var ms = new MemoryStream(data.Length);
        int at = 0;
        foreach (var (start, end) in drop)
        {
            ms.Write(data, at, start - at);
            ms.WriteByte((byte)' ');
            at = end;
        }
        ms.Write(data, at, data.Length - at);
        return (ms.ToArray(), drop.Count);
    }

    private static byte[] Join(IReadOnlyList<byte[]> streams)
    {
        using var ms = new MemoryStream();
        foreach (var s in streams) { ms.Write(s); ms.WriteByte((byte)'\n'); }
        return ms.ToArray();
    }
}
