using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfEditor.ContentStream;

/// <summary>쪽의 /Contents(스트림 하나 또는 배열)를 풀어 한 덩어리로 잇는다. 스트림 사이에는 줄바꿈을 넣는다(연산자가 경계에 걸치지 않게 — 규격상 토큰은 경계를 넘지 못한다).</summary>
public static class PageContent
{
    public sealed record Loaded(byte[]? Data, string? Failure);

    public static Loaded Load(PdfPage page)
    {
        try
        {
            var item = Resolve(page.Elements["/Contents"]);
            if (item is null) return new Loaded([], null);
            using var ms = new MemoryStream();
            if (item is PdfArray arr)
            {
                foreach (var e in arr.Elements)
                {
                    if (Resolve(e) is not PdfDictionary d || d.Stream is null) return new Loaded(null, "contents element is not a stream");
                    ms.Write(d.Stream.UnfilteredValue);
                    ms.WriteByte((byte)'\n');
                }
            }
            else if (item is PdfDictionary single && single.Stream is not null)
            {
                ms.Write(single.Stream.UnfilteredValue);
                ms.WriteByte((byte)'\n');
            }
            else return new Loaded(null, "contents is not a stream");
            return new Loaded(ms.ToArray(), null);
        }
        catch (Exception e) { return new Loaded(null, "contents:" + e.GetType().Name); }
    }

    /// <summary>Do 연산자의 XObject 이름 → 종류(이미지/폼) 해석기. 쪽 /Resources /XObject에서 찾는다.</summary>
    public static Func<string, XObjectInfo?> XObjectResolver(PdfPage page)
    {
        PdfDictionary? xobjs = null;
        try { xobjs = page.Resources?.Elements.GetDictionary("/XObject"); } catch { }
        return name =>
        {
            if (xobjs is null) return null;
            try
            {
                var x = xobjs.Elements.GetDictionary("/" + name);
                if (x is null) return null;
                string? sub = x.Elements.GetName("/Subtype");
                if (sub == "/Image") return new XObjectInfo(ObjectKind.Image, null, null);
                if (sub != "/Form") return null;
                Mat? m = null; (double, double, double, double)? bbox = null;
                var ma = x.Elements.GetArray("/Matrix");
                if (ma is { Elements.Count: 6 }) m = new Mat(N(ma, 0), N(ma, 1), N(ma, 2), N(ma, 3), N(ma, 4), N(ma, 5));
                var ba = x.Elements.GetArray("/BBox");
                if (ba is { Elements.Count: 4 }) bbox = (N(ba, 0), N(ba, 1), N(ba, 2), N(ba, 3));
                return new XObjectInfo(ObjectKind.Form, m, bbox);
            }
            catch { return null; }
        };
    }

    private static double N(PdfArray a, int i) => a.Elements.GetReal(i);

    private static PdfItem? Resolve(PdfItem? i) => i is PdfReference r ? r.Value : i;
}

/// <summary>Do로 그리는 XObject 한 개의 종류와(폼이면) /Matrix·/BBox.</summary>
public sealed record XObjectInfo(ObjectKind Kind, Mat? FormMatrix, (double L, double B, double R, double T)? BBox);
