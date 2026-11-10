namespace PdfEditor.ContentStream;

/// <summary>
/// 쪽 내용의 구조 고치기. 짝 없는 Q(저장한 그래픽 상태가 없는데 복원)는 PDFium·PDF-XChange가 무시하지만 Acrobat은 오류로 보고 쪽의 나머지를 그리지 않는다.
/// 0.8.0까지의 저장(PDFium이 여러 스트림 중 편집한 스트림만 다시 씀)이 이런 파일을 만들었다(2026-11-10 사용자 PDF) — 그런 파일을 다시 저장할 때 고친다.
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
}
