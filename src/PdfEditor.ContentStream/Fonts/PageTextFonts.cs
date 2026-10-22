using PdfSharp.Pdf;
using SharpPage = PdfSharp.Pdf.PdfPage;

namespace PdfEditor.ContentStream.Fonts;

/// <summary>쪽 내용 스트림 안의 글 객체 하나: 글꼴 사전·글 상태(자간·낱말 간격·가로 비율)·보인 문자열 조각(M5 C8의 재료).</summary>
public sealed record TextObjectFont(PdfDictionary Font, string FontRes, double Tf, double Tc, double Tw, double Tz,
    IReadOnlyList<ReadOnlyMemory<byte>> Strings, (double X, double Y)? Origin)
{
    /// <summary>글 객체가 보인 문자열 바이트를 모두 이은 것(코드북이 ToUnicode 없이 배울 때 PDFium 글과 짝지음).</summary>
    public byte[] AllBytes => Strings.SelectMany(s => s.ToArray()).ToArray();
}

/// <summary>
/// PDFium 글 객체 번호 → 내용 스트림 안의 그 글 객체(M5 코드북 재료, V16 규칙). PDFium을 부르지 않으므로 PDFium 객체 종류 열(<c>pdfiumKinds</c>)을 받는다.
/// 해석기가 센 객체 종류 열이 PDFium과 같을 때만 번호가 맞다고 본다 — 다르면 null(+이유).
/// </summary>
public static class PageTextFonts
{
    public static Dictionary<int, TextObjectFont>? Build(SharpPage page, IReadOnlyList<ObjectKind> pdfiumKinds, out string? failure)
    {
        failure = null;
        var loaded = PageContent.Load(page);
        if (loaded.Data is null) { failure = loaded.Failure; return null; }
        var pc = ContentLexer.Parse(loaded.Data);
        var seg = ContentSegmenter.Segment(pc, PageContent.XObjectResolver(page));
        if (!seg.Ok) { failure = seg.Failure; return null; }
        if (seg.Objects.Count != pdfiumKinds.Count)
        {
            failure = $"object sequence differs (ours {seg.Objects.Count}, pdfium {pdfiumKinds.Count})";
            return null;
        }
        for (int i = 0; i < pdfiumKinds.Count; i++)
            if (seg.Objects[i].Kind != pdfiumKinds[i]) { failure = $"object kind differs at {i}"; return null; }

        var fonts = page.Resources?.Elements.GetDictionary("/Font");
        var map = new Dictionary<int, TextObjectFont>();
        for (int i = 0; i < seg.Objects.Count; i++)
        {
            var o = seg.Objects[i];
            if (o.Kind != ObjectKind.Text || o.Text is not { } snap || snap.FontOp < 0) continue;
            var tf = pc.Ops[snap.FontOp];
            string? name = tf.Operands.FirstOrDefault(x => x.Kind == OperandKind.Name)?.Text;
            if (name is null || fonts is null) continue;
            var fd = fonts.Elements.GetDictionary("/" + name);
            if (fd is null) continue;
            double size = tf.Operands.LastOrDefault(x => x.Kind == OperandKind.Number)?.Number ?? 0;
            var strings = new List<ReadOnlyMemory<byte>>();
            void Add(Operand x)
            {
                if (x.Kind == OperandKind.String && x.Bytes is { } b) strings.Add(b);
                else if (x.Kind == OperandKind.Array) foreach (var y in x.Items) Add(y);
            }
            var show = pc.Ops[o.OpIndex];
            if (show.Name is "Tj" or "TJ") foreach (var x in show.Operands) Add(x);
            else if (show.Operands.Count > 0) Add(show.Operands[^1]);
            map[i] = new TextObjectFont(fd, name, size, snap.Tc, snap.Tw, snap.Tz, strings, o.Origin);
        }
        return map;
    }
}
