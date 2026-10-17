using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;

namespace PdfEditor.Appearance;

public abstract record PathSeg;
public sealed record MoveTo(PdfPointF P) : PathSeg;
public sealed record LineTo(PdfPointF P) : PathSeg;
public sealed record CubicTo(PdfPointF C1, PdfPointF C2, PdfPointF P) : PathSeg;
public sealed record ClosePath : PathSeg;

public enum LineCap { Butt = 0, Round = 1, Square = 2 }
public enum LineJoin { Miter = 0, Round = 1, Bevel = 2 }

public sealed record PaintOp(
    ImmutableArray<PathSeg> Path, PdfColor? Fill, PdfColor? Stroke, float StrokeWidth, LineCap Cap, LineJoin Join);

/// <summary>같은 글자 크기·색의 글리프 묶음(BT ... ET 한 번). FontResource는 AP 리소스 이름(예: "F1").</summary>
public sealed record TextRunOp(string FontResource, float SizePt, PdfColor Color, ImmutableArray<PositionedGlyph> Glyphs);

/// <summary>이미지 XObject 한 장을 Rect(PDF 사용자 좌표)에 채워 그린다. ImageId는 AnnotationAssets의 키.</summary>
public sealed record ImageOp(string ImageResource, string ImageId, PdfRectF Rect);

/// <summary>
/// 화면과 PDF AP가 함께 쓰는 그리기 목록(PDF 사용자 좌표). Bounds는 경로 점(제어점 포함)을 선 두께/2만큼 넓힌 경계.
/// 그리는 순서: 경로(Ops) → 그림(Images) → 글자(Texts).
/// </summary>
public sealed record AppearanceDrawing(ImmutableArray<PaintOp> Ops, float Opacity, PdfRectF Bounds)
{
    public ImmutableArray<TextRunOp> Texts { get; init; } = ImmutableArray<TextRunOp>.Empty;
    public ImmutableArray<ImageOp> Images { get; init; } = ImmutableArray<ImageOp>.Empty;

    /// <summary>글자 묶음이 쓴 글자의 코드포인트·CID·폭·원문. 글꼴 서브셋을 만들 때 쓴다(PatchPlanBuilder).</summary>
    public FontUse CollectFontUse()
    {
        var cps = new SortedSet<int>();
        var glyphs = new List<GlyphUse>();
        var seen = new HashSet<(int, string)>();
        foreach (var run in Texts)
            foreach (var g in run.Glyphs)
            {
                foreach (var r in g.Text.EnumerateRunes()) cps.Add(r.Value);
                if (!seen.Add((g.Cid, g.Text))) continue;
                glyphs.Add(new GlyphUse(g.Cid, run.SizePt > 0 ? (int)Math.Round(g.Advance * 1000f / run.SizePt) : 0, g.Text));
            }
        return new FontUse([.. cps], [.. glyphs]);
    }
}
