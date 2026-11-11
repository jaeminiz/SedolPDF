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
        var (data, restores) = RemoveUnmatchedRestores(joined);
        if (fragments == 0 && restores == 0) return (null, 0, 0);
        return (data ?? joined, fragments, restores);
    }

    private static byte[] Join(IReadOnlyList<byte[]> streams)
    {
        using var ms = new MemoryStream();
        foreach (var s in streams) { ms.Write(s); ms.WriteByte((byte)'\n'); }
        return ms.ToArray();
    }
}
