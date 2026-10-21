namespace PdfEditor.ContentStream;

/// <summary>PDFium이 알려 준 객체 하나(종류 + 쪽 공간 경계 사각형). 호출자가 PDFium 객체 목록에서 채운다 — 이 모듈은 PDFium을 부르지 않는다.</summary>
public readonly record struct ReferenceObject(ObjectKind Kind, Box Bounds);

/// <summary>PDFium 객체 번호 ↔ 해석기 객체 대응 검증 결과. <see cref="Ok"/>일 때만 <c>Objects[번호]</c>를 믿고 쓴다.</summary>
public sealed class ObjectMapResult
{
    public required bool Ok { get; init; }
    public string? Reason { get; init; }
    /// <summary>처음 어긋난 PDFium 객체 번호(없으면 -1).</summary>
    public int FirstMismatch { get; init; } = -1;
    public IReadOnlyList<PageObject> Objects { get; init; } = [];
    /// <summary>경계를 실제로 비교한 객체 수 / 비교할 수 없어 종류만 본 객체 수(글 위치 불명·셰이딩·폼 BBox 없음).</summary>
    public int BoundsChecked { get; init; }
    public int BoundsSkipped { get; init; }

    /// <summary>PDFium 객체 번호에 해당하는 해석기 객체. 대응이 검증되지 않았으면 예외.</summary>
    public PageObject this[int pdfiumIndex] =>
        Ok ? Objects[pdfiumIndex] : throw new InvalidOperationException("object map is not verified: " + Reason);
}

/// <summary>
/// 안전망: 해석기 객체 목록이 PDFium 객체 목록과 (개수·종류 순서·경계)까지 맞는지 확인한다.
/// 하나라도 어긋나면 그 쪽은 새 방식(내용 스트림 고쳐 쓰기)을 쓰지 않는다 — 번호가 다른 객체를 가리키면 엉뚱한 객체를 지우게 되기 때문.
/// </summary>
public static class ObjectMap
{
    /// <summary>경계 허용 오차(pt): 1.5 + 큰 변의 1 % + 선 굵기의 2배(PDFium은 선 끝·이음 모양에 따라 경계를 조금 부풀린다 — 실제 PDF로 확인).</summary>
    public static double Tolerance(double width, double height, double strokeWidth) =>
        1.5 + 0.01 * Math.Max(width, height) + 2 * strokeWidth;

    public static ObjectMapResult Verify(Segmentation ours, IReadOnlyList<ReferenceObject> pdfium)
    {
        if (!ours.Ok) return Bad("segmentation failed: " + ours.Failure, -1, ours);
        if (ours.Objects.Count != pdfium.Count) return Bad($"object count {ours.Objects.Count} != pdfium {pdfium.Count}", Math.Min(ours.Objects.Count, pdfium.Count), ours);
        int checkedN = 0, skipped = 0;
        // 폼은 PDFium 경계가 /BBox 밖까지 나가는 일이 있어(실제 PDF로 확인) 경계를 비교하지 않고 종류·순서만 본다.
        for (int i = 0; i < pdfium.Count; i++)
        {
            var o = ours.Objects[i]; var r = pdfium[i];
            if (o.Kind != r.Kind) return Bad($"kind {o.Kind} != pdfium {r.Kind} at {i}", i, ours);
            switch (o.Kind)
            {
                case ObjectKind.Path or ObjectKind.Image when o.Bounds is { } b:
                {
                    double w = Math.Max(b.Width, r.Bounds.Width), h = Math.Max(b.Height, r.Bounds.Height);
                    double tol = Tolerance(w, h, o.StrokeWidth);
                    // 줄을 그린 경로: PDFium은 뾰족한 꺾임 등으로 경계를 우리 계산보다 훨씬 더 부풀릴 수 있다(실제 PDF에서 짧은 굵은 선이 선 굵기의 100배 가까이) → 부풀림은 크게, 줄어듦은 작게 허용.
                    double grow = o.StrokeWidth > 0 ? tol + 150 * o.StrokeWidth : tol;
                    var pb = r.Bounds;
                    if (pb.L - b.L > tol || b.R - pb.R > tol || pb.B - b.B > tol || b.T - pb.T > tol ||          // PDFium 상자가 더 작음
                        b.L - pb.L > grow || pb.R - b.R > grow || b.B - pb.B > grow || pb.T - b.T > grow)          // PDFium 상자가 더 큼
                        return Bad($"{o.Kind} bounds differ at {i}: ours {F(b)} pdfium {F(r.Bounds)} tol {tol:0.##} sw {o.StrokeWidth:0.##}", i, ours);
                    checkedN++;
                    break;
                }
                case ObjectKind.Text when o.Origin is { } p && (r.Bounds.Width > 0.01 || r.Bounds.Height > 0.01):   // 폭·높이 0 상자(공백 등)는 위치 정보가 없다
                {
                    // PDFium 글 상자는 글자 잉크에 가깝게 작아서(점·하이픈은 기준선 위에 뜸) 기준선 원점이 상자 밖일 수 있다 → 상자까지 거리가 글꼴 크기 안쪽이면 통과.
                    double dx = Math.Max(Math.Max(r.Bounds.L - p.X, p.X - r.Bounds.R), 0), dy = Math.Max(Math.Max(r.Bounds.B - p.Y, p.Y - r.Bounds.T), 0);
                    double tol = 2 + 1.2 * o.DeviceFontSize + 0.01 * r.Bounds.Width;
                    if (Math.Sqrt(dx * dx + dy * dy) > tol)
                        return Bad($"text origin far from pdfium box at {i}: origin ({p.X:0.#},{p.Y:0.#}) pdfium {F(r.Bounds)} fs {o.DeviceFontSize:0.#}", i, ours);
                    checkedN++;
                    break;
                }
                default:
                    skipped++;
                    break;
            }
        }
        return new ObjectMapResult { Ok = true, Objects = ours.Objects, BoundsChecked = checkedN, BoundsSkipped = skipped };
    }

    private static string F(Box b) => $"[{b.L:0.#},{b.B:0.#},{b.R:0.#},{b.T:0.#}]";

    private static ObjectMapResult Bad(string reason, int index, Segmentation ours) =>
        new() { Ok = false, Reason = reason, FirstMismatch = index, Objects = ours.Objects };
}
