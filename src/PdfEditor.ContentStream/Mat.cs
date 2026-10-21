using System.Globalization;

namespace PdfEditor.ContentStream;

/// <summary>PDF 변환 행렬 [a b c d e f] (행 벡터 규칙: 점 p' = p·M). <see cref="Then"/>은 "this 다음에 o".</summary>
public readonly record struct Mat(double A, double B, double C, double D, double E, double F)
{
    public static readonly Mat Identity = new(1, 0, 0, 1, 0, 0);

    public static Mat Translate(double tx, double ty) => new(1, 0, 0, 1, tx, ty);

    public static Mat Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    public double Determinant => A * D - B * C;

    /// <summary>this를 먼저, o를 나중에 적용한 합성 (PDF의 `this × o`).</summary>
    public Mat Then(Mat o) => new(
        A * o.A + B * o.C, A * o.B + B * o.D,
        C * o.A + D * o.C, C * o.B + D * o.D,
        E * o.A + F * o.C + o.E, E * o.B + F * o.D + o.F);

    public (double X, double Y) Apply(double x, double y) => (x * A + y * C + E, x * B + y * D + F);

    public bool IsFinite => double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F);

    /// <summary>역행렬. 특이(행렬식이 0에 가까움) 행렬이면 null.</summary>
    public Mat? Invert()
    {
        double det = Determinant;
        double scale = Math.Max(Math.Abs(A * D), Math.Abs(B * C));
        if (!double.IsFinite(det) || Math.Abs(det) <= 1e-9 * Math.Max(scale, 1e-300) || Math.Abs(det) < 1e-12) return null;
        double ia = D / det, ib = -B / det, ic = -C / det, id = A / det;
        var r = new Mat(ia, ib, ic, id, -(E * ia + F * ic), -(E * ib + F * id));
        return r.IsFinite ? r : null;
    }

    /// <summary>`a b c d e f cm` 형태의 문자열(소수 8자리까지, 문화권 무관).</summary>
    public string ToOperands() => string.Join(' ', new[] { A, B, C, D, E, F }.Select(Fmt));

    internal static string Fmt(double v)
    {
        string s = v.ToString("0.########", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}
