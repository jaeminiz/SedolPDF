using System.Runtime.InteropServices;

namespace Pdf.Native.Tests;

/// <summary>V7: PDFium 157 (chromium/8100) 공개 헤더에 레이어(OC) 전환 함수가 있는지 확인한다.</summary>
public class V7LayerApiProbeTests
{
    // 헤더(fpdfview/fpdf_doc/fpdf_edit/fpdf_save)에서 OC·Layer·OptionalContent 이름은 찾지 못함.
    // 존재 여부를 추측으로 흔한 이름들도 함께 조사한다.
    private static readonly string[] Candidates =
    {
        "FPDFOC_GetOCGCount", "FPDF_GetOCGs", "FPDF_OCG_GetVisible", "FPDF_OCG_SetVisible",
        "FPDF_SetOCGVisible", "FPDF_GetLayerCount", "FPDFCatalog_GetOptionalContent",
        "FPDF_GetOptionalContentGroups", "FPDF_OCContext_Create", "FPDF_GetOCConfig",
        "FPDFPageObj_GetMarkedContentID",   // 대조군: 있어야 함 (마크드 콘텐츠 ID 조회일 뿐 레이어 전환 아님)
    };

    [Fact]
    public void LayerToggleApi_NotExported()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "pdfium.dll");
        if (!File.Exists(path)) path = Directory.GetFiles(AppContext.BaseDirectory, "pdfium.dll", SearchOption.AllDirectories).First();
        var lib = NativeLibrary.Load(path);
        try
        {
            var found = new List<string>();
            foreach (var n in Candidates)
            {
                bool ok = NativeLibrary.TryGetExport(lib, n, out _);
                Console.WriteLine($"[V7] {n}: {(ok ? "EXPORTED" : "absent")}");
                if (ok) found.Add(n);
            }
            Assert.Contains("FPDFPageObj_GetMarkedContentID", found);   // 탐침 자체가 동작하는지
            Assert.Equal(new[] { "FPDFPageObj_GetMarkedContentID" }, found.ToArray());
        }
        finally { NativeLibrary.Free(lib); }
    }
}
