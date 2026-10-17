using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Exchange.Xfdf;

namespace PdfEditor.App.Documents;

/// <summary>
/// 활성 문서의 모든 주석(편집 가능한 모델 + 읽기 전용 원본 정보, 답글·상태 포함)을 XFDF 파일로 쓴다.
/// 읽기만 하므로 암호 문서에서도 된다. 임시 파일에 쓴 뒤 바꿔 넣어 반쯤 쓴 파일이 남지 않게 한다.
/// </summary>
public static class XfdfExporter
{
    /// <summary>내보낸 주석 수(읽기 전용 원본 포함)를 돌려준다.</summary>
    public static async Task<int> ExportAsync(DocumentSession s, AnnotationAssets assets, string path)
    {
        await s.AnnotationsReady;
        var doc = Build(s, assets);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await Task.Run(() => doc.Save(fs));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
        return doc.Root!.Element(XfdfWriter.Ns + "annots")!.Elements().Count();
    }

    public static System.Xml.Linq.XDocument Build(DocumentSession s, AnnotationAssets assets)
    {
        var all = new List<(int PageIndex, Annotation A)>();
        var readOnly = new List<(int PageIndex, ReadOnlyAnnotationInfo Info)>();
        var pages = s.Pages;
        for (int i = 0; i < pages.Length; i++)
        {
            var snap = s.Annotations.Get(pages[i].Id);
            foreach (var v in snap.Items) all.Add((i, v.Data));
            foreach (var r in snap.ReadOnly) readOnly.Add((i, r));
        }
        return XfdfWriter.Write(all, s.DisplayName, assets, readOnly);
    }
}
