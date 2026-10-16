using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document.Annotations;

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

/// <summary>화면과 PDF AP가 함께 쓰는 그리기 목록(PDF 사용자 좌표). Bounds는 경로 점(제어점 포함)을 선 두께/2만큼 넓힌 경계.</summary>
public sealed record AppearanceDrawing(ImmutableArray<PaintOp> Ops, float Opacity, PdfRectF Bounds);
