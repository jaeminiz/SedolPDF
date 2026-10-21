using System.Globalization;
using System.Text;
using PdfEditor.ContentStream;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.Document.Content;

namespace PdfEditor.Appearance.Patching;

/// <summary>쪽 끝에 덧붙일 새 객체 하나. Content의 "{F}"는 글꼴 자원 이름, "{I}"는 이미지 XObject 이름으로 바뀐다.</summary>
public sealed record NewSnippet(string Content, FontUse? Font, string? ImageAssetId);

/// <summary>한 쪽의 편집 기록을 내용 스트림 고쳐 쓰기용으로 풀어 놓은 것(M4x). <see cref="Failure"/>가 있으면 이 방식으로 저장할 수 없다.</summary>
public sealed class ContentRewritePlan
{
    public string? Failure { get; init; }
    public bool Ok => Failure is null;
    /// <summary>원본 객체(PDFium 객체 번호)에 하는 끄기·옮기기.</summary>
    public IReadOnlyList<ObjectEdit> ObjectEdits { get; init; } = [];
    /// <summary>쪽 끝에 덧붙일 새 객체(도형·이미지·앱 글꼴 줄). 화면과 같은 순서: 앱 글꼴 줄이 맨 위.</summary>
    public IReadOnlyList<NewSnippet> Snippets { get; init; } = [];
}

/// <summary>
/// 편집 기록(<see cref="ContentEdit"/>)을 ① 원본 객체 편집 ② 새 객체 조각으로 나눈다. 같은 객체에 쌓인 끄기·옮기기·도형 스타일은 한 번에 접는다.
/// 이 방식이 못 하는 편집(원래 글꼴 글 넣기 — U-C, 원본 도형 스타일 바꾸기)은 실패로 돌려주고 호출자는 저장을 막는다.
/// </summary>
public static class ContentRewritePlanner
{
    private sealed class Ins
    {
        public required int Order;
        public required char Kind;   // S 도형, I 이미지, T 앱 글꼴 줄
        public ShapeSpec? Shape;
        public InsertImageEdit? Image;
        public InsertAppTextEdit? Text;
        public Mat M = Mat.Identity;
        public bool Disabled;
    }

    public static ContentRewritePlan Plan(IReadOnlyList<ContentEdit> edits)
    {
        var origDisabled = new HashSet<int>();
        var origThen = new Dictionary<int, Mat>();
        var ins = new Dictionary<long, Ins>();
        int order = 0;
        foreach (var e in edits)
        {
            switch (e)
            {
                case InsertShapeEdit sh: ins[sh.Id] = new Ins { Order = order++, Kind = 'S', Shape = sh.Shape }; break;
                case InsertImageEdit im: ins[im.Id] = new Ins { Order = order++, Kind = 'I', Image = im }; break;
                case InsertAppTextEdit at: ins[at.Id] = new Ins { Order = order++, Kind = 'T', Text = at }; break;
                case InsertTextLikeEdit: return Fail("inserting text in the original font needs the PDFium path (app font only here)");
                case DeactivateEdit d:
                    if (d.Target.IsInserted)
                    {
                        if (!ins.TryGetValue(d.Target.InsertedId, out var t)) return Fail("deactivate of an unknown inserted object");
                        t.Disabled = true;
                    }
                    else origDisabled.Add(d.Target.OriginalIndex);
                    break;
                case TransformEdit tr:
                {
                    var m = ToMat(tr.Then);
                    if (tr.Target.IsInserted)
                    {
                        if (!ins.TryGetValue(tr.Target.InsertedId, out var t)) return Fail("transform of an unknown inserted object");
                        t.M = t.M.Then(m);
                    }
                    else origThen[tr.Target.OriginalIndex] = origThen.GetValueOrDefault(tr.Target.OriginalIndex, Mat.Identity).Then(m);
                    break;
                }
                case SetShapeStyleEdit ss:
                    if (!ss.Target.IsInserted || !ins.TryGetValue(ss.Target.InsertedId, out var s) || s.Kind != 'S')
                        return Fail("changing the style of an original shape needs the PDFium path");
                    s.Shape = s.Shape! with { Style = ss.Style };
                    break;
                default: return Fail("unsupported edit " + e.GetType().Name);
            }
        }

        var objEdits = new List<ObjectEdit>();
        foreach (int i in origDisabled.Union(origThen.Keys).OrderBy(x => x))
        {
            if (origDisabled.Contains(i)) objEdits.Add(new DisableObject(i));
            else if (origThen[i] != Mat.Identity) objEdits.Add(new TransformObject(i, origThen[i]));
        }

        var snippets = new List<NewSnippet>();
        foreach (var x in ins.Values.Where(v => !v.Disabled && v.Kind != 'T').OrderBy(v => v.Order)) snippets.Add(Snippet(x));
        foreach (var x in ins.Values.Where(v => !v.Disabled && v.Kind == 'T').OrderBy(v => v.Order)) snippets.Add(Snippet(x));
        return new ContentRewritePlan { ObjectEdits = objEdits, Snippets = snippets };
    }

    private static NewSnippet Snippet(Ins x)
    {
        var sb = new StringBuilder();
        switch (x.Kind)
        {
            case 'S':
                if (x.M != Mat.Identity) sb.Append(Ops(x.M)).Append(" cm\n");
                Shape(sb, x.Shape!);
                return new NewSnippet(sb.ToString(), null, null);
            case 'I':
            {
                var p = ToMat(x.Image!.Placement).Then(x.M);
                sb.Append(Ops(p)).Append(" cm\n/{I} Do\n");
                return new NewSnippet(sb.ToString(), null, x.Image.AssetId);
            }
            default:
            {
                var t = x.Text!;
                if (x.M != Mat.Identity) sb.Append(Ops(x.M)).Append(" cm\n");
                sb.Append(t.Line.Content);
                if (sb[^1] != '\n') sb.Append('\n');
                return new NewSnippet(sb.ToString(), t.Line.Font, null);
            }
        }
    }

    private const double Kappa = 0.5522847;

    private static void Shape(StringBuilder sb, ShapeSpec s)
    {
        var st = s.Kind == ShapeKind.Line ? s.Style with { Fill = null } : s.Style;
        if (st.Stroke is { } c) sb.Append(Rgb(c.R, c.G, c.B)).Append(" RG ").Append(F(st.Width)).Append(" w\n");
        if (st.Fill is { } f) sb.Append(Rgb(f.R, f.G, f.B)).Append(" rg\n");
        switch (s.Kind)
        {
            case ShapeKind.Line:
                sb.Append($"{F(s.A.X)} {F(s.A.Y)} m {F(s.B.X)} {F(s.B.Y)} l\n");
                break;
            case ShapeKind.Ellipse:
            {
                var r = s.Bounds;
                double cx = (r.Left + r.Right) / 2.0, cy = (r.Bottom + r.Top) / 2.0, rx = r.Width / 2.0, ry = r.Height / 2.0, kx = rx * Kappa, ky = ry * Kappa;
                sb.Append($"{F(cx + rx)} {F(cy)} m\n");
                sb.Append($"{F(cx + rx)} {F(cy + ky)} {F(cx + kx)} {F(cy + ry)} {F(cx)} {F(cy + ry)} c\n");
                sb.Append($"{F(cx - kx)} {F(cy + ry)} {F(cx - rx)} {F(cy + ky)} {F(cx - rx)} {F(cy)} c\n");
                sb.Append($"{F(cx - rx)} {F(cy - ky)} {F(cx - kx)} {F(cy - ry)} {F(cx)} {F(cy - ry)} c\n");
                sb.Append($"{F(cx + kx)} {F(cy - ry)} {F(cx + rx)} {F(cy - ky)} {F(cx + rx)} {F(cy)} c\nh\n");
                break;
            }
            default:
            {
                var r = s.Bounds;
                sb.Append($"{F(r.Left)} {F(r.Bottom)} m {F(r.Right)} {F(r.Bottom)} l {F(r.Right)} {F(r.Top)} l {F(r.Left)} {F(r.Top)} l h\n");
                break;
            }
        }
        bool fill = st.Fill is not null, stroke = st.Stroke is not null;
        sb.Append(fill && stroke ? "B" : fill ? "f" : stroke ? "S" : "n").Append('\n');
    }

    private static string Rgb(byte r, byte g, byte b) => $"{F(r / 255.0)} {F(g / 255.0)} {F(b / 255.0)}";
    private static string F(double v)
    {
        string s = v.ToString("0.#####", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
    private static string Ops(Mat m) => string.Join(' ', new[] { m.A, m.B, m.C, m.D, m.E, m.F }.Select(F));
    private static Mat ToMat(Pdf.Native.PdfMatrix m) => new(m.A, m.B, m.C, m.D, m.E, m.F);
    private static ContentRewritePlan Fail(string why) => new() { Failure = why };
}
