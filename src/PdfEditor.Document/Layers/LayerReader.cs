using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Document.Layers;

/// <summary>레이어(선택 가능한 내용 그룹, OCG) 하나. InitiallyVisible은 파일이 정한 처음 표시 상태.</summary>
public sealed record LayerInfo(string Name, bool InitiallyVisible);

/// <summary>
/// /OCProperties의 레이어 목록을 읽는다. PDFium에는 레이어 API가 없어서(검증 V7) PDFsharp로 읽는다.
/// 읽기 전용이며 PDFium을 쓰지 않으므로 어느 스레드에서 불러도 된다.
/// </summary>
public static class LayerReader
{
    /// <summary>열 수 없는 파일이면 예외를 던진다. /OCProperties가 없으면 빈 목록.</summary>
    public static IReadOnlyList<LayerInfo> Read(byte[] pdfBytes, string? password)
    {
        using var ms = new MemoryStream(pdfBytes, writable: false);
        // 사용자 암호로 Import 모드가 열리는지는 검증 필요 (V6에서 Modify는 소유자 암호가 필요했음).
        using var doc = password is null
            ? PdfReader.Open(ms, PdfDocumentOpenMode.Import)
            : PdfReader.Open(ms, password, PdfDocumentOpenMode.Import);

        var ocProps = doc.Internals.Catalog.Elements.GetDictionary("/OCProperties");
        var ocgs = ocProps?.Elements.GetArray("/OCGs");
        if (ocProps is null || ocgs is null) return [];

        var d = ocProps.Elements.GetDictionary("/D");
        bool baseOff = string.Equals(d?.Elements.GetName("/BaseState"), "/OFF", StringComparison.Ordinal);
        var off = ObjectNumbers(d?.Elements.GetArray("/OFF"));
        var on = ObjectNumbers(d?.Elements.GetArray("/ON"));

        var result = new List<LayerInfo>();
        foreach (var item in ocgs.Elements)
        {
            if (item is not PdfReference r || r.Value is not PdfDictionary ocg) continue;
            string name = ocg.Elements.GetString("/Name");
            int id = r.ObjectNumber;
            bool visible = baseOff ? on.Contains(id) : !off.Contains(id);
            result.Add(new LayerInfo(name, visible));
        }
        return result;
    }

    private static HashSet<int> ObjectNumbers(PdfArray? array)
    {
        var set = new HashSet<int>();
        if (array is null) return set;
        foreach (var item in array.Elements)
            if (item is PdfReference r) set.Add(r.ObjectNumber);
        return set;
    }
}
