using System.Text;
using PdfEditor.ContentStream;
using PdfEditor.ContentStream.Fonts;
using PdfEditor.TestFixtures;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfEditor.TextEdit.Tests;

/// <summary>V22/V23 표본이 도면 번호를 어떻게 쓰는가.</summary>
internal enum IdCase
{
    /// <summary>① 한 글 연산에 번호 통째(CID 서브셋, ToUnicode 있음).</summary>
    Whole,
    /// <summary>② TJ 배열이 번호를 둘로 쪼갬(글자 간격 숫자).</summary>
    TjArray,
    /// <summary>③ 글자마다 별도 글 연산(별도 BT 블록).</summary>
    PerGlyph,
    /// <summary>④ 90° 회전 글(표제란).</summary>
    Rot90,
    /// <summary>⑤ 쪽 /Rotate 90 + 글 90°.</summary>
    PageRot90,
    /// <summary>⑥' 비임베드 단순 글꼴, 한 BT 안에서 Tj가 이어짐.</summary>
    SimpleChain,
    /// <summary>⑥'' 비임베드 단순 글꼴, TJ 간격 숫자.</summary>
    SimpleTj,
    /// <summary>⑦ CID 서브셋이지만 ToUnicode를 지움(찾기 불가).</summary>
    NoToUnicode,
}

/// <summary>
/// 도면 번호 찾아 바꾸기(0.4.0 V22/V23) 합성 표본 1쪽(A4). 같은 쪽에 번호가 세 번 나온다:
/// 주 번호(케이스별 모양, 14 pt), 통째 복사본(10 pt), 문장 속(“REF {id} REV A”). 미끼: 번호가 앞부분인 더 긴 번호(id+"1"), 소문자, 공백 낀 번호, 다른 번호.
/// 범례(0~9, H, -, 영문)는 같은 글꼴로 한 줄 쓴다(legend). 없으면 새 번호에 쓸 글자가 서브셋에 없을 수 있다.
/// 정답: (대소문자 구분+완전일치) 3 / (무시+부분일치) 5 / (무시+완전일치) 4.
/// </summary>
internal static class DrawingIdPdfFactory
{
    static DrawingIdPdfFactory() => System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TestPdfFactory).TypeHandle);

    private static readonly XPdfFontOptions Uni = new(PdfFontEncoding.Unicode);

    public const string Id = "H-2501";
    public static IdCase RefCaseOf(IdCase c) => c switch
    {
        IdCase.TjArray or IdCase.PerGlyph or IdCase.NoToUnicode => IdCase.Whole,
        IdCase.SimpleTj => IdCase.SimpleChain,
        _ => c,
    };

    /// <param name="refLayout">기준본 배치: 문장에서 번호 뒤 글(" REV A")을 원래 번호(<see cref="Id"/>) 기준 자리에 따로 둔다 — 바꾸기는 뒤 글을 옮기지 않으므로(D8).</param>
    public static void Create(string path, IdCase c, string id = Id, bool legend = true, bool refLayout = false)
    {
        if (c is IdCase.SimpleChain or IdCase.SimpleTj) { CreateRaw(path, c, id, legend, refLayout); return; }
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            if (c == IdCase.PageRot90) page.Rotate = 90;
            var big = new XFont("Arial", 14, XFontStyleEx.Regular, Uni);
            var small = new XFont("Arial", 10, XFontStyleEx.Regular, Uni);
            var label = new XFont("Arial", 8, XFontStyleEx.Regular, Uni);
            using (var g = XGraphics.FromPdfPage(page))
            {
                g.DrawString("HULL NO.", label, XBrushes.Black, new XPoint(100, 700));
                switch (c)
                {
                    case IdCase.PerGlyph:
                        for (int i = 0; i < id.Length; i++)
                            g.DrawString(id[i].ToString(), big, XBrushes.Black, new XPoint(160 + g.MeasureString(id[..i], big).Width, 700));
                        break;
                    case IdCase.Rot90:
                    case IdCase.PageRot90:
                    {
                        var st = g.Save();
                        g.RotateAtTransform(-90, new XPoint(400, 700));
                        g.DrawString(id, big, XBrushes.Black, new XPoint(400, 700));
                        g.Restore(st);
                        break;
                    }
                    default:
                        g.DrawString(id, big, XBrushes.Black, new XPoint(160, 700));
                        break;
                }
                g.DrawString(id, small, XBrushes.Black, new XPoint(100, 600));
                if (refLayout)
                {
                    g.DrawString($"REF {id}", small, XBrushes.Black, new XPoint(100, 560));
                    g.DrawString(" REV A", small, XBrushes.Black, new XPoint(100 + g.MeasureString($"REF {Id}", small).Width, 560));
                }
                else g.DrawString($"REF {id} REV A", small, XBrushes.Black, new XPoint(100, 560));
                g.DrawString(Id + "1", small, XBrushes.Black, new XPoint(100, 520));
                g.DrawString("h-2501", small, XBrushes.Black, new XPoint(100, 500));
                g.DrawString("H 2501", small, XBrushes.Black, new XPoint(100, 480));
                g.DrawString("H-2502", small, XBrushes.Black, new XPoint(100, 460));
                if (legend) g.DrawString("0123456789 H-ABCDEFGR", big, XBrushes.Black, new XPoint(100, 740));
            }
            doc.Save(path);
        }
        if (c == IdCase.TjArray) SplitMainIntoTj(path, id);
        if (c == IdCase.NoToUnicode) StripAllToUnicode(path);
    }

    /// <summary>글 연산이 번호를 통째로 보이는 첫 Tj를 `[(앞) -45 (뒤)] TJ`로 쪼갠다.</summary>
    private static void SplitMainIntoTj(string path, string id)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        var page = doc.Pages[0];
        var data = PageContent.Load(page).Data!;
        var pc = ContentLexer.Parse(data);
        var fonts = page.Resources!.Elements.GetDictionary("/Font")!;
        string? cur = null;
        var strings = new Dictionary<string, List<ReadOnlyMemory<byte>>>();
        foreach (var op in pc.Ops)
        {
            if (op.Name == "Tf") cur = op.Operands.First(o => o.Kind == OperandKind.Name).Text;
            else if (op.Name == "Tj" && cur is not null)
            {
                if (!strings.TryGetValue(cur, out var l)) strings[cur] = l = [];
                l.Add(op.Operands[^1].Bytes);
            }
        }
        var books = strings.ToDictionary(kv => kv.Key, kv => FontCodebook.TryBuild(fonts.Elements.GetDictionary("/" + kv.Key)!, kv.Value));
        cur = null;
        foreach (var op in pc.Ops)
        {
            if (op.Name == "Tf") cur = op.Operands.First(o => o.Kind == OperandKind.Name).Text;
            if (op.Name != "Tj" || cur is null || books[cur] is not { } book) continue;
            var b = op.Operands[^1].Bytes;
            if (book.Decode(b) != id) continue;
            int cut = b.Length / 2 / 2 * 2;   // 2바이트 코드, 대략 절반
            string h1 = Convert.ToHexString(b, 0, cut), h2 = Convert.ToHexString(b, cut, b.Length - cut);
            var sb = new StringBuilder();
            sb.Append(Encoding.Latin1.GetString(data, 0, op.Start)).Append($"[<{h1}> -45 <{h2}>] TJ").Append(Encoding.Latin1.GetString(data, op.End, data.Length - op.End));
            page.Contents.Elements.Clear();
            page.Contents.AppendContent().CreateStream(Encoding.Latin1.GetBytes(sb.ToString()));
            doc.Save(path);
            return;
        }
        throw new InvalidOperationException("main id Tj not found");
    }

    private static void StripAllToUnicode(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        foreach (var o in doc.Internals.GetAllObjects())
            if (o is PdfDictionary d && d.Elements.GetName("/Subtype") == "/Type0") d.Elements.Remove("/ToUnicode");
        doc.Save(path);
    }

    /// <summary>비임베드 Helvetica(/Widths 포함) 원시 쪽.</summary>
    private static void CreateRaw(string path, IdCase c, string id, bool legend, bool refLayout)
    {
        var sb = new StringBuilder();
        sb.Append("BT /F1 8 Tf 100 142 Td (HULL NO.) Tj ET\n");
        if (c == IdCase.SimpleChain)
        {
            sb.Append("BT /F1 14 Tf 160 142 Td ");
            for (int i = 0; i < id.Length; i += 2) sb.Append('(').Append(id.Substring(i, Math.Min(2, id.Length - i))).Append(") Tj ");
            sb.Append("ET\n");
        }
        else
        {
            sb.Append("BT /F1 14 Tf 160 142 Td [(").Append(id[..1]).Append(") -20 (").Append(id[1..^1]).Append(") 15 (").Append(id[^1]).Append(")] TJ ET\n");
        }
        sb.Append($"BT /F1 10 Tf 100 242 Td ({id}) Tj ET\n");
        if (refLayout)
        {
            double x = 100 + $"REF {Id}".Sum(ch => RawWidth(ch)) * 10 / 1000.0;
            sb.Append($"BT /F1 10 Tf 100 282 Td (REF {id}) Tj ET\n");
            sb.Append(System.FormattableString.Invariant($"BT /F1 10 Tf {x:0.###} 282 Td ( REV A) Tj ET\n"));
        }
        else sb.Append($"BT /F1 10 Tf 100 282 Td (REF {id} REV A) Tj ET\n");
        sb.Append($"BT /F1 10 Tf 100 322 Td ({Id}1) Tj ET\n");
        sb.Append("BT /F1 10 Tf 100 342 Td (h-2501) Tj ET\n");
        sb.Append("BT /F1 10 Tf 100 362 Td (H 2501) Tj ET\n");
        sb.Append("BT /F1 10 Tf 100 382 Td (H-2502) Tj ET\n");
        if (legend) sb.Append("BT /F1 14 Tf 100 102 Td (0123456789 H-ABCDEFGR) Tj ET\n");
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        var res = new PdfDictionary(doc);
        page.Elements["/Resources"] = res;
        var fonts = new PdfDictionary(doc);
        res.Elements["/Font"] = fonts;
        var f = new PdfDictionary(doc);
        f.Elements["/Type"] = new PdfName("/Font");
        f.Elements["/Subtype"] = new PdfName("/Type1");
        f.Elements["/BaseFont"] = new PdfName("/Helvetica");
        f.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        f.Elements["/FirstChar"] = new PdfInteger(32);
        f.Elements["/LastChar"] = new PdfInteger(126);
        var w = new PdfArray(doc);
        for (int ch = 32; ch <= 126; ch++) w.Elements.Add(new PdfInteger(RawWidth((char)ch)));
        f.Elements["/Widths"] = w;
        doc.Internals.AddObject(f);
        fonts.Elements["/F1"] = f.Reference!;
        page.Contents.AppendContent().CreateStream(Encoding.Latin1.GetBytes(sb.ToString()));
        doc.Save(path);
    }

    public static int RawWidth(char ch) => ch switch { ' ' => 278, '-' => 333, '.' => 278, 'i' or 'l' => 222, >= 'A' and <= 'Z' => 667, 'h' => 556, _ => 556 };
}
