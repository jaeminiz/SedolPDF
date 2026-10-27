using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Content;
using PdfEditor.TestFixtures;
using PdfEditor.TextEdit.Paragraphs;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

// M5 사용자 검수용 샘플 PDF 생성기. 사용: dotnet run --project spikes/M5.UserSamples -- <출력 폴더>
// 회사 도면은 쓰지 않는다. 모든 글은 직접 쓴 예시 문장이다. 끝에 PDFium으로 열어 문단 인식 결과를 점검한다.
System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TestPdfFactory).TypeHandle);

string outDir = args.Length > 0 ? args[0] : Path.Combine("spikes", "out", "m5-user-test");
Directory.CreateDirectory(outDir);
const string TestPassword = "test1234";
bool verifyOnly = args.Contains("--verify");   // 이미 만든 .pdf만 점검

var uni = new XPdfFontOptions(PdfFontEncoding.Unicode);
XFont F(string fam, double size, bool bold = false) => new(fam, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular, uni);
var ko = F("Malgun Gothic", 10.5);
var arial = F("Arial", 11);
var georgia = F("Georgia", 11);

string[] KO =
[
    "도면 검토 절차는 먼저 접수된 도면의 개정 이력을 확인하고, 이전 승인본과 비교하여 변경된 부분을 표시한 뒤, 관련 부서의 의견을 수렴하여 최종 승인을 받는 순서로 진행한다. 모든 변경 사항은 검토 기록에 남겨야 하며, 승인되지 않은 도면은 현장에 배포할 수 없다.",
    "배관 지지대는 지지대 목록에 따라 설치하며, 기준 위치에서 오십 밀리미터 이상 벗어난 경우에는 실제 설치 상태를 그린 스케치와 함께 담당 기술자에게 즉시 보고하여야 한다. 보고가 지연되면 후속 공정의 일정에 영향을 줄 수 있으므로 주의한다.",
    "밸브 작동기는 정격 압력 상태에서 시험하고 각 방향의 작동 시간을 세 번씩 측정한다. 세 번의 평균값을 시운전 기록지에 기입하고 제작사가 보증한 값과 비교하여 허용 오차를 벗어나면 재조정한 뒤 다시 시험한다.",
    "예비품은 장비 번호별로 납품 문서에 기재하며, 각 품목에는 제작사 부품 번호와 이 년간 운전에 필요한 공급 수량 그리고 선박이 선주에게 인도될 때까지의 권장 보관 조건이 함께 적혀 있어야 한다.",
];
string[] EN =
[
    "The review procedure begins by checking the revision history of every drawing received, comparing it against the previously approved issue, marking the parts that have changed and collecting the comments of each department before the final approval is signed.",
    "All modifications must be recorded in the inspection log, and a drawing that has not been approved shall never be released to the workshop floor, because the cost of rework after cutting steel is far larger than the cost of waiting for a signature.",
    "Spare parts are listed per equipment tag in the delivery documents and each item carries the maker part number, the quantity supplied for two years of operation and the recommended storage condition until the vessel is delivered to the owner.",
];

// ---- 그리기 도우미 ----
List<string> Wrap(XGraphics g, string text, XFont f, double width)
{
    var lines = new List<string>();
    string cur = "";
    foreach (var t in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        string cand = cur.Length == 0 ? t : cur + " " + t;
        if (cur.Length == 0 || g.MeasureString(cand, f, XStringFormats.Default).Width <= width) cur = cand;
        else { lines.Add(cur); cur = t; }
    }
    if (cur.Length > 0) lines.Add(cur);
    return lines;
}
double Para(XGraphics g, string text, XFont f, double x, double y, double width, double pitch)
{
    foreach (var l in Wrap(g, text, f, width))
    {
        g.DrawString(l, f, XBrushes.Black, new XPoint(x, y), XStringFormats.BaseLineLeft);
        y += pitch;
    }
    return y;
}
void Text(XGraphics g, string s, XFont f, double x, double y) => g.DrawString(s, f, XBrushes.Black, new XPoint(x, y), XStringFormats.BaseLineLeft);
void Rule(XGraphics g, double x1, double y1, double x2, double y2) => g.DrawLine(new XPen(XColors.Black, 0.7), x1, y1, x2, y2);

void Make(string name, double w, double h, Action<XGraphics> draw, int rotate = 0, string? password = null)
{
    if (verifyOnly) return;
    using var doc = new PdfSharp.Pdf.PdfDocument();
    var page = doc.AddPage();
    page.Width = XUnit.FromPoint(w);
    page.Height = XUnit.FromPoint(h);
    using (var g = XGraphics.FromPdfPage(page)) draw(g);
    if (rotate != 0) page.Rotate = rotate;
    if (password is not null)
    {
        doc.SecuritySettings.UserPassword = password;
        doc.SecuritySettings.OwnerPassword = password + "-owner";
    }
    using var ms = new MemoryStream();
    doc.Save(ms, false);
    for (int attempt = 1; ; attempt++)
    {
        try { File.WriteAllBytes(Path.Combine(outDir, name + ".pdf.tmp"), ms.ToArray()); break; }
        catch (UnauthorizedAccessException) when (attempt < 8) { Thread.Sleep(500); }   // 백신·색인기가 새 파일을 잠시 잡는 경우가 있어 다시 시도
    }
}

// 01 한글 입력(IME): 한글 문단 3개
Make("01-ime", 595, 842, g =>
{
    double y = 100;
    for (int i = 0; i < 3; i++) y = Para(g, KO[i], ko, 60, y, 440, 16) + 12;
});

// 02 표: 위에 평범한 문단 하나(비교용) + 선 있는 표제란 + 선 없는 표
Make("02-table", 1191, 842, g =>
{
    Para(g, KO[0], ko, 80, 100, 520, 16);
    var lab = F("Malgun Gothic", 7);
    var val = F("Malgun Gothic", 10);
    double x0 = 700, y0 = 300, cw = 120, ch = 38;
    string[] labels = ["도면 이름", "도면 번호", "축척", "쪽", "작성", "검토", "승인", "날짜"];
    string[] values = ["배관 배치도", "DWG-1234-0051", "1:50", "3 / 12", "박재민", "박세현", "김철수", "2026-10-22"];
    for (int i = 0; i < 8; i++)
    {
        double x = x0 + (i % 4) * cw, y = y0 + (i / 4) * ch;
        Text(g, labels[i], lab, x + 4, y + 9);
        Text(g, values[i], val, x + 4, y + 26);
    }
    for (int r = 0; r <= 2; r++) Rule(g, x0, y0 + r * ch, x0 + 4 * cw, y0 + r * ch);
    for (int c = 0; c <= 4; c++) Rule(g, x0 + c * cw, y0, x0 + c * cw, y0 + 2 * ch);
    var f9 = F("Malgun Gothic", 9);
    string[][] cols =
    [
        ["번호", "P-101", "P-102", "V-201", "V-202"],
        ["용도", "해수 펌프", "연료유 펌프", "청수 밸브", "윤활유 밸브"],
        ["용량", "250 M3/H", "40 M3/H", "15 M3/H", "8 M3/H"],
        ["비고", "필수", "예비", "필수", "수동"],
    ];
    for (int c = 0; c < 4; c++)
        for (int r = 0; r < 5; r++) Text(g, cols[c][r], f9, 100 + c * 110, 420 + r * 18);
});

// 03 틀리게 묶이기 쉬운 문단: 두 문단 사이 빈 줄 없음 + 문단 바로 밑에 붙은 서명 줄
Make("03-merged", 595, 842, g =>
{
    double y = 100;
    Text(g, "[ 첫째 묶음 - 문단 두 개가 붙어 있음 ]", F("Malgun Gothic", 8), 60, 80);
    y = Para(g, KO[1], ko, 60, y, 440, 16);
    y = Para(g, KO[2], ko, 60, y, 440, 16);
    y += 50;
    Text(g, "[ 둘째 묶음 - 문단 바로 밑에 서명 줄 ]", F("Malgun Gothic", 8), 60, y - 20);
    y = Para(g, KO[3], ko, 60, y, 440, 16);
    Text(g, "2026년 10월 22일 작성자 박재민", ko, 60, y);
});

// 04 영문 글꼴 문단(한글 입력용): Arial 영문 문단 2개 + Georgia(명조 계열) 영문 문단 1개
Make("04-subset", 595, 842, g =>
{
    double y = 100;
    y = Para(g, EN[0], arial, 60, y, 475, 15) + 14;
    y += 26;
    Text(g, "(아래는 명조 계열 글꼴 문단)", F("Malgun Gothic", 8), 60, y - 14);
    Para(g, EN[2], georgia, 60, y, 475, 15);
});

// 05 두 번 고치기
Make("05-twice", 595, 842, g =>
{
    double y = 100;
    y = Para(g, KO[0], ko, 60, y, 440, 16) + 14;
    Para(g, KO[1], ko, 60, y, 440, 16);
});

// 06a 회전된 쪽(90도)
Make("06a-rotated90", 595, 842, g =>
{
    double y = 100;
    for (int i = 0; i < 3; i++) y = Para(g, KO[i], ko, 60, y, 440, 16) + 12;
}, rotate: 90);

// 06b 큰 배율용: 글이 작고 문단이 많음
Make("06b-zoom", 595, 842, g =>
{
    double y = 80;
    var small = F("Malgun Gothic", 9);
    for (int i = 0; i < 4; i++) y = Para(g, KO[i], small, 50, y, 495, 13) + 10;
    Para(g, KO[0], small, 50, y + 10, 495, 13);
});

// 06c 암호 PDF (테스트 전용 비밀번호)
Make("06c-encrypted", 595, 842, g =>
{
    double y = 100;
    for (int i = 0; i < 3; i++) y = Para(g, KO[i], ko, 60, y, 440, 16) + 12;
}, password: TestPassword);

// 참고: 이 PC에서는 프로그램이 .pdf 파일을 바로 쓰면 접근 거부가 나서 .pdf.tmp로 쓴 뒤 이름을 바꾼다(스크립트 build-user-samples.ps1).
// ---- 점검: PDFium으로 열어 문단 인식 결과를 찍는다 ----
Console.WriteLine($"만든 곳: {Path.GetFullPath(outDir)}");
using var t = new PdfiumThread();
foreach (var path in Directory.GetFiles(outDir, "*.pdf").Order())
{
    string name = Path.GetFileName(path);
    try
    {
        var s = await DocumentSession.OpenAsync(t, path, name.Contains("encrypted") ? TestPassword : null);
        var pageId = s.Pages[0].Id;
        var report = await t.InvokeAsync(() =>
        {
            var idx = s.GetObjectIndex(pageId);
            var texts = idx.Entries.Where(e => e.Type == PdfPageObjectType.Text && !e.Ref.IsInserted).ToList();
            var sb = new System.Text.StringBuilder();
            foreach (var e in texts)
            {
                var d = ParagraphReader.Detect(s, pageId, e.Ref, idx);
                if (d.Model is not { } m) { sb.AppendLine("  (모델 없음)"); continue; }
                
                if (!name.StartsWith("0[23]") && false) continue;
                sb.AppendLine($"  시작={GetT(s, pageId, e.Ref)} -> {m.Lines.Length}줄 신뢰도 {m.Confidence:0.00} 문단 {m.IsParagraph} 사유 {m.LowConfidenceReason}");
            }
            return $"{texts.Count}개 글 객체\n{sb}";
        });
        Console.WriteLine($"{name}: 열림, {report}");
    }
    catch (Exception ex) { Console.WriteLine($"{name}: 오류 {ex.GetType().Name} {ex.Message}"); }
}

static string GetT(DocumentSession s, PageId id, ObjectRef r) { var x = s.GetObjectText(id, r) ?? ""; return x[..Math.Min(8, x.Length)]; }
