using System.Text;
using Pdf.Native;

namespace S3.TextReplace;

/// <summary>
/// 저장소 밖(private/ 폴더, 커밋 제외)의 실제 도면으로 S3 절차를 돌려 수치만 기록한다.
/// 파일명·내용은 결과에 적지 않는다. private/ 에 PDF가 없으면 아무것도 하지 않는다.
/// 파일명에 "한글"이 든 PDF가 있으면 그것을 우선 조사한다(선택에만 쓰고 결과에는 적지 않음).
/// </summary>
public class DrawingProbe : IDisposable
{
    private readonly PdfiumThread _t = new();
    public void Dispose() => _t.Dispose();

    private sealed class Tally
    {
        public int[] ByType = new int[8];       // 1 text, 2 path, 3 image, 4 shading, 5 form
        public int Sampled, Readable, Failed;
        public int OwnCovered, OwnNotCovered;   // 객체 자신의 글자가 폰트 cmap에 모두 있는가
        public int HangulObjs, HangulCovered, HangulNotCovered;
    }

    private static string? PrivateDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PdfEditor.sln"))) dir = dir.Parent;
        var p = dir is null ? null : Path.Combine(dir.FullName, "private");
        return p is not null && Directory.Exists(p) ? p : null;
    }

    private static bool HasHangul(string s) => s.Any(c => c >= '가' && c <= '힣');

    private static void Walk(IntPtr obj, IntPtr textPage, Tally t, int budget)
    {
        int type = TextNative.FPDFPageObj_GetType(obj);
        t.ByType[Math.Clamp(type, 0, 7)]++;
        if (type == TextNative.ObjText && t.Sampled < budget)
        {
            t.Sampled++;
            try
            {
                var own = TextReplacer.ObjectText(obj, textPage);
                var missing = own.Length == 0 ? [] : TextReplacer.MissingGlyphs(obj, own);
                t.Readable++;
                if (own.Length > 0)
                {
                    if (missing.Length == 0) t.OwnCovered++; else t.OwnNotCovered++;
                    if (HasHangul(own))
                    {
                        t.HangulObjs++;
                        if (missing.Length == 0) t.HangulCovered++; else t.HangulNotCovered++;
                    }
                }
            }
            catch { t.Failed++; }
        }
        else if (type == 5)
        {
            int n = TextNative.FPDFFormObj_CountObjects(obj);
            for (int i = 0; i < n; i++)
                Walk(TextNative.FPDFFormObj_GetObject(obj, (nuint)i), textPage, t, budget);
        }
    }

    [Fact]
    public async Task Private_Drawings_Probe()
    {
        var dir = PrivateDir();
        var all = dir is null ? [] : Directory.GetFiles(dir, "*.pdf");
        var korean = all.Where(f => Path.GetFileName(f).Contains("한글")).ToArray();
        var files = (korean.Length > 0 ? korean : all).Take(2).ToArray();
        if (files.Length == 0) return;

        var sb = new StringBuilder("# S3 실제 도면 확인 (파일명·내용 비공개, 수치만)\n\n");
        sb.AppendLine("앞 5쪽의 객체를 폼 XObject 안까지 따라 들어가며 센 값입니다. 텍스트 객체는 최대 300개까지 조사했습니다.");
        sb.AppendLine("'자기 글자 모두 있음'은 그 텍스트 객체가 실제로 쓰는 글자가 임베디드 폰트의 cmap에 전부 있는지입니다 (없으면 판정 불가 또는 누락).\n");
        sb.AppendLine("| 도면 | 쪽 수 | 텍스트 | 경로 | 이미지 | 조사 | 자기 글자 모두 있음 | 없거나 판정불가 | 한글 포함 객체 | 그중 모두 있음 | 그중 없거나 판정불가 | 읽기 실패 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        int idx = 0;
        foreach (var file in files)
        {
            idx++;
            var row = await _t.InvokeAsync(() =>
            {
                using var doc = PdfDocument.Open(_t, file);
                var t = new Tally();
                for (int p = 0; p < Math.Min(doc.PageCount, 5); p++)
                {
                    using var page = doc.LoadPage(p);
                    var tp = TextReplacer.LoadTextPage(page);
                    try
                    {
                        int n = TextReplacer.ObjectCount(page);
                        for (int i = 0; i < n; i++)
                            Walk(TextNative.FPDFPage_GetObject(page.Handle, i), tp, t, 300);
                    }
                    finally { TextReplacer.CloseTextPage(tp); }
                }
                return $"| {idx} | {doc.PageCount} | {t.ByType[1]} | {t.ByType[2]} | {t.ByType[3]} | {t.Sampled} | {t.OwnCovered} | {t.OwnNotCovered} | {t.HangulObjs} | {t.HangulCovered} | {t.HangulNotCovered} | {t.Failed} |";
            });
            sb.AppendLine(row);
        }
        File.WriteAllText(Path.Combine(TestOut.Dir(), "s3-private-report.md"), sb.ToString());
    }
}
