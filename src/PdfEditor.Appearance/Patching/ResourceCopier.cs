using PdfEditor.ContentStream;
using PdfEditor.Document.Output;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfEditor.Appearance.Patching;

/// <summary>
/// 다른 쪽에서 복사한 조각이 쓰는 자원(글꼴·XObject·ExtGState·색 공간·패턴·셰이딩)을 대상 쪽 /Resources에 옮긴다(0.9.0 개체 복사).
/// 같은 이름이 같은 객체를 가리키면 그대로 쓰고, 이름이 겹치는데 다른 객체면 <c>SedolR{n}</c>으로 새 이름을 붙여 조각 안의 이름 피연산자를 바꾼다.
/// 원천에 자원이 없으면 ContentRegenerationException("resource /X not found on source page").
/// </summary>
internal static class ResourceCopier
{
    private static readonly Dictionary<string, string> Keys = new()
    {
        ["Font"] = "/Font", ["XObject"] = "/XObject", ["ExtGState"] = "/ExtGState", ["ColorSpace"] = "/ColorSpace",
        ["Pattern"] = "/Pattern", ["Shading"] = "/Shading", ["Properties"] = "/Properties",
    };

    /// <summary>쪽의 /Resources. 쪽에 없으면 /Parent 쪽 트리에서 물려받은 것을 찾는다(없으면 null).</summary>
    public static PdfDictionary? FindResources(PdfPage page)
    {
        PdfDictionary? d = page;
        for (int guard = 0; d is not null && guard < 32; guard++)
        {
            if (d.Elements.GetDictionary("/Resources") is { } r) return r;
            d = d.Elements.GetDictionary("/Parent");
        }
        return null;
    }

    /// <param name="dryRun">true면 대상 쪽을 바꾸지 않고 원천에 자원이 있는지만 본다(내용은 그대로 돌려줌).</param>
    public static string CopyInto(PdfDictionary targetResources, PdfDictionary? sourceResources, string snippet,
        IReadOnlyList<ResourceUse> uses, PdfDocument doc, int pageNumber, bool dryRun = false)
    {
        var rename = new Dictionary<ResourceUse, string>();
        foreach (var use in uses)
        {
            if (!Keys.TryGetValue(use.Category, out var key)) throw new ContentRegenerationException(pageNumber, "unknown resource category " + use.Category);
            var srcCat = sourceResources?.Elements.GetDictionary(key);
            var srcItem = srcCat?.Elements["/" + use.Name];
            if (srcItem is null) throw new ContentRegenerationException(pageNumber, $"resource /{use.Name} not found on source page");
            if (dryRun) continue;

            var tgtCat = targetResources.Elements.GetDictionary(key);
            if (tgtCat is null) { tgtCat = new PdfDictionary(doc); targetResources.Elements[key] = tgtCat; }
            var existing = tgtCat.Elements["/" + use.Name];
            if (existing is not null && Same(existing, srcItem)) continue;
            string name = use.Name;
            if (existing is not null)
            {
                int n = 1;
                while (tgtCat.Elements.ContainsKey("/SedolR" + n) || rename.ContainsValue("SedolR" + n)) n++;
                name = "SedolR" + n;
                rename[use] = name;
            }
            tgtCat.Elements["/" + name] = srcItem is PdfReference ? srcItem : ((PdfObject)srcItem).Clone();
        }
        return rename.Count == 0 ? snippet : ObjectSnippet.Rename(snippet, rename);
    }

    private static bool Same(PdfItem a, PdfItem b)
    {
        if (a is PdfReference ra && b is PdfReference rb) return ra.ObjectID == rb.ObjectID;
        if (a is PdfReference || b is PdfReference) return false;
        return a.ToString() == b.ToString();
    }
}
