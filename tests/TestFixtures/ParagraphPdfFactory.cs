using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfEditor.TestFixtures;

/// <summary>정답표 한 줄: 같은 ParagraphId를 가진 줄은 같은 문단이다. 문단 하나에 한 줄뿐이면 "묶으면 안 되는 줄"이다.</summary>
/// <param name="Text">줄의 글(공백 제거해 비교).</param>
/// <param name="ParagraphId">정답 문단 번호(쪽 안에서 유일).</param>
/// <param name="InTable">표 칸 안의 줄(표 병합 오탐 판정용).</param>
/// <param name="Overlaid">겹쳐 찍은(굵게 흉내) 줄.</param>
public sealed record TruthLine(string Text, int ParagraphId, bool InTable = false, bool Overlaid = false);

/// <summary>M5 V17~V21 시험용 샘플 한 쪽: 파일 + 정답표. Kind = "Document" | "Drawing".</summary>
public sealed record ParagraphSample(string Name, string Kind, string Path, IReadOnlyList<TruthLine> Truth, string Note);

/// <summary>
/// M5(문단 편집) 기술 검증 샘플. 문서형(영문·한글 양쪽 맞춤·왼쪽 맞춤·글자 단위 줄바꿈·제목+글머리표·2단·섞인 굵은 글·겹쳐 찍은 글·가운데/오른쪽)과
/// 도면형(표제란·표·주기·90° 글·치수 흩어짐·폼 안 글·보이지 않는 글)을 만든다. 각 샘플은 1쪽이고 정답(줄 → 문단 번호)을 함께 돌려준다.
/// 글 폭은 PDFsharp 측정값이라 줄바꿈이 실제 글꼴 폭과 같다.
/// </summary>
public static class ParagraphPdfFactory
{
    static ParagraphPdfFactory() => System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TestPdfFactory).TypeHandle);

    private static readonly XPdfFontOptions Uni = new(PdfFontEncoding.Unicode);

    private static readonly string[] En =
    [
        "The review procedure begins by checking the revision history of every drawing received, comparing it against the previously approved issue, marking the parts that have changed and collecting the comments of each department before the final approval is signed.",
        "All modifications must be recorded in the inspection log, and a drawing that has not been approved shall never be released to the workshop floor, because the cost of rework after cutting steel is far larger than the cost of waiting for a signature.",
        "Piping supports are located according to the support schedule, and any deviation greater than fifty millimetres from the nominal position must be reported to the engineer in charge together with a sketch showing the actual installed condition.",
        "Valve actuators are tested with the system at rated pressure and the stroke time is measured three times in each direction; the average of the three readings is entered in the commissioning sheet and compared with the value guaranteed by the maker.",
        "Spare parts are listed per equipment tag in the delivery documents and each item carries the maker part number, the quantity supplied for two years of operation and the recommended storage condition until the vessel is delivered to the owner.",
    ];

    private static readonly string[] Ko =
    [
        "도면 검토 절차는 먼저 접수된 도면의 개정 이력을 확인하고, 이전 승인본과 비교하여 변경된 부분을 표시한 뒤, 관련 부서의 의견을 수렴하여 최종 승인을 받는 순서로 진행한다. 모든 변경 사항은 검토 기록에 남겨야 하며, 승인되지 않은 도면은 현장에 배포할 수 없다.",
        "배관 지지대는 지지대 목록에 따라 설치하며, 기준 위치에서 오십 밀리미터 이상 벗어난 경우에는 실제 설치 상태를 그린 스케치와 함께 담당 기술자에게 즉시 보고하여야 한다. 보고가 지연되면 후속 공정의 일정에 영향을 줄 수 있으므로 주의한다.",
        "밸브 작동기는 정격 압력 상태에서 시험하고 각 방향의 작동 시간을 세 번씩 측정한다. 세 번의 평균값을 시운전 기록지에 기입하고 제작사가 보증한 값과 비교하여 허용 오차를 벗어나면 재조정한 뒤 다시 시험한다.",
        "예비품은 장비 번호별로 납품 문서에 기재하며, 각 품목에는 제작사 부품 번호와 이 년간 운전에 필요한 공급 수량 그리고 선박이 선주에게 인도될 때까지의 권장 보관 조건이 함께 적혀 있어야 한다.",
        "교육 자료의 각 장은 개요와 구성 요소, 작동 원리, 점검 방법 순서로 정리하였으며 그림과 표는 장마다 번호를 새로 매긴다. 처음 보는 용어는 쪽 아래 각주에 풀이를 달아 신입 사원도 쉽게 이해할 수 있도록 하였다.",
    ];

    /// <summary>쪽 하나를 그리는 도우미. 글줄을 그릴 때마다 정답표에 쌓는다.</summary>
    private sealed class PageBuilder(PdfDocument doc, PdfPage page, XGraphics g)
    {
        public readonly PdfDocument Doc = doc;
        public readonly PdfPage Page = page;
        public readonly XGraphics G = g;
        public readonly List<TruthLine> Truth = [];
        private int _pid;
        public int NewPid() => ++_pid;

        public static XFont Font(string family, double size, bool bold = false) =>
            new(family, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular, Uni);

        public double Width(string s, XFont f) => G.MeasureString(s, f, XStringFormats.Default).Width;

        public void Draw(string s, XFont f, double x, double y, XBrush? brush = null) =>
            G.DrawString(s, f, brush ?? XBrushes.Black, new XPoint(x, y), XStringFormats.BaseLineLeft);

        /// <summary>한 줄을 통째로(글 객체 하나) 그린다.</summary>
        public void Line(string text, XFont f, double x, double y, int pid, bool table = false, bool overlay = false)
        {
            Draw(text, f, x, y);
            if (overlay) Draw(text, f, x + 0.4, y);
            Truth.Add(new TruthLine(text, pid, table, overlay));
        }

        /// <summary>낱말마다 글 객체 하나로 그린다(양쪽 맞춤 흉내: width를 채우게 틈을 늘림).</summary>
        public void WordsLine(IReadOnlyList<string> words, XFont f, double x, double y, double width, int pid, bool justify = true)
        {
            double sum = words.Sum(w => Width(w, f)), gap = words.Count > 1 && justify ? (width - sum) / (words.Count - 1) : Width(" ", f);
            double cx = x;
            foreach (var w in words) { Draw(w, f, cx, y); cx += Width(w, f) + gap; }
            Truth.Add(new TruthLine(string.Join(' ', words), pid));
        }

        public List<string> Wrap(string text, XFont f, double width, double indent, bool chars)
        {
            var lines = new List<string>();
            var tokens = chars ? text.Select(c => c.ToString()).ToList() : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            string cur = "";
            foreach (var tok in tokens)
            {
                if (chars && cur.Length == 0 && tok == " ") continue;
                double avail = width - (lines.Count == 0 ? indent : 0);
                string cand = cur.Length == 0 ? tok : chars ? cur + tok : cur + " " + tok;
                if (cur.Length == 0 || Width(cand, f) <= avail) cur = cand;
                else { lines.Add(cur.TrimEnd()); cur = chars && tok == " " ? "" : tok; }
            }
            if (cur.Length > 0) lines.Add(cur.TrimEnd());
            return lines;
        }

        /// <summary>문단 하나. align: L 왼쪽 / J 양쪽(낱말 객체) / C 가운데 / R 오른쪽. 다음 줄 기준선 y를 돌려준다.</summary>
        public double Paragraph(string text, XFont f, double x, double y, double width, double pitch, char align = 'L', double indent = 0,
            bool chars = false, bool overlay = false, int? pid = null, bool table = false)
        {
            int id = pid ?? NewPid();
            var lines = Wrap(text, f, width, indent, chars);
            for (int i = 0; i < lines.Count; i++)
            {
                double lx = x + (i == 0 ? indent : 0), avail = width - (i == 0 ? indent : 0);
                string s = lines[i];
                bool last = i == lines.Count - 1;
                switch (align)
                {
                    case 'J' when !last && !chars:
                        WordsLine(s.Split(' ', StringSplitOptions.RemoveEmptyEntries), f, lx, y, avail, id);
                        break;
                    case 'C': Line(s, f, x + (width - Width(s, f)) / 2, y, id, table, overlay); break;
                    case 'R': Line(s, f, x + width - Width(s, f), y, id, table, overlay); break;
                    default: Line(s, f, lx, y, id, table, overlay); break;
                }
                y += pitch;
            }
            return y;
        }

        public void Rule(double x1, double y1, double x2, double y2, double w = 0.7) => G.DrawLine(new XPen(XColors.Black, w), x1, y1, x2, y2);
    }

    private static ParagraphSample Build(string dir, string name, string kind, double w, double h, string note, Action<PageBuilder> draw)
    {
        string path = System.IO.Path.Combine(dir, name + ".pdf");
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(w);
        page.Height = XUnit.FromPoint(h);
        PageBuilder pb;
        using (var g = XGraphics.FromPdfPage(page))
        {
            pb = new PageBuilder(doc, page, g);
            draw(pb);
        }
        doc.Save(path);
        return new ParagraphSample(name, kind, path, pb.Truth, note);
    }

    /// <summary>모든 샘플을 dir에 만든다.</summary>
    public static IReadOnlyList<ParagraphSample> CreateAll(string dir)
    {
        Directory.CreateDirectory(dir);
        var list = new List<ParagraphSample>();
        var arial = PageBuilder.Font("Arial", 11);
        var arialB = PageBuilder.Font("Arial", 11, true);
        var malgun = PageBuilder.Font("Malgun Gothic", 10.5);

        // ---------- 문서형 ----------
        list.Add(Build(dir, "doc-en-left", "Document", 595, 842, "영문 본문 왼쪽 맞춤, 줄마다 글 객체 하나(Arial TrueType 서브셋)", p =>
        {
            double y = 90;
            foreach (var t in En.Take(4)) y = p.Paragraph(t, arial, 60, y, 475, 15) + 9;
        }));
        list.Add(Build(dir, "doc-en-justified", "Document", 595, 842, "영문 양쪽 맞춤(낱말마다 글 객체), 첫 줄 들여쓰기 문단 포함", p =>
        {
            double y = 90;
            for (int i = 0; i < 4; i++) y = p.Paragraph(En[i], arial, 60, y, 475, 15, 'J', indent: i % 2 == 1 ? 24 : 0) + (i % 2 == 1 ? 0 : 9);
        }));
        list.Add(Build(dir, "doc-ko-justified", "Document", 595, 842, "한글 양쪽 맞춤(낱말 객체), 첫 줄 들여쓰기, 문단 간격 0(들여쓰기만으로 구분) + 간격 있는 문단", p =>
        {
            double y = 90;
            y = p.Paragraph(Ko[0], malgun, 60, y, 475, 16, 'J', indent: 10.5);
            y = p.Paragraph(Ko[1], malgun, 60, y, 475, 16, 'J', indent: 10.5);
            y = p.Paragraph(Ko[2], malgun, 60, y, 475, 16, 'J', indent: 10.5) + 10;
            y = p.Paragraph(Ko[3], malgun, 60, y, 475, 16, 'J') + 10;
            p.Paragraph(Ko[4], malgun, 60, y, 475, 16, 'J');
        }));
        list.Add(Build(dir, "doc-ko-left-wordwrap", "Document", 595, 842, "한글 왼쪽 맞춤, 어절 단위 줄바꿈, 줄마다 글 객체 하나", p =>
        {
            double y = 90;
            for (int i = 0; i < 4; i++) y = p.Paragraph(Ko[i], malgun, 60, y, 440, 16) + 10;
        }));
        list.Add(Build(dir, "doc-ko-charwrap", "Document", 595, 842, "한글 글자 단위 줄바꿈(어절 중간에서도 끊김), 왼쪽 맞춤", p =>
        {
            double y = 90;
            for (int i = 0; i < 4; i++) y = p.Paragraph(Ko[i], malgun, 60, y, 440, 16, chars: true) + 10;
        }));
        list.Add(Build(dir, "doc-heading-bullets", "Document", 595, 842, "제목 + 본문 + 소제목 + 글머리표 목록(항목 = 문단)", p =>
        {
            double y = 90;
            var h1 = PageBuilder.Font("Arial", 18, true);
            p.Line("Commissioning Report", h1, 60, y, p.NewPid()); y += 30;
            y = p.Paragraph(En[0], arial, 60, y, 475, 15) + 10;
            var h2 = PageBuilder.Font("Arial", 13, true);
            p.Line("Checklist items", h2, 60, y, p.NewPid()); y += 22;
            foreach (var item in new[] { En[2], En[3], En[4] })
            {
                int pid = p.NewPid();
                var lines = p.Wrap(item, arial, 461, 0, false);
                for (int i = 0; i < lines.Count; i++)
                {
                    if (i == 0) p.Draw("•", arial, 66, y);
                    p.Draw(lines[i], arial, 80, y);
                    p.Truth.Add(new TruthLine(i == 0 ? "• " + lines[i] : lines[i], pid));
                    y += 15;
                }
                y += 4;
            }
        }));
        list.Add(Build(dir, "doc-2col", "Document", 595, 842, "2단 편집: 같은 기준선에 두 단의 줄이 나란히", p =>
        {
            double y1 = 90, y2 = 90;
            for (int i = 0; i < 3; i++) y1 = p.Paragraph(En[i], arial, 50, y1, 240, 15) + 9;
            for (int i = 3; i < 5; i++) y2 = p.Paragraph(En[i], arial, 310, y2, 240, 15, 'L') + 9;
        }));
        list.Add(Build(dir, "doc-mixed-bold", "Document", 595, 842, "한 줄 안에 굵은 낱말이 섞임: 줄마다 따로(묶으면 안 되는 사례)", p =>
        {
            double y = 90;
            var bold = PageBuilder.Font("Arial", 11, true);
            foreach (var t in En.Take(3))
            {
                var lines = p.Wrap(t, arial, 475, 0, false);
                foreach (var l in lines)
                {
                    var words = l.Split(' ');
                    int bi = Math.Min(2, words.Length - 1);
                    double x = 60;
                    for (int i = 0; i < words.Length; i++)
                    {
                        var f = i == bi ? bold : arial;
                        p.Draw(words[i], f, x, y);
                        x += p.Width(words[i] + " ", f);
                    }
                    p.Truth.Add(new TruthLine(l, p.NewPid()));
                    y += 15;
                }
                y += 9;
            }
        }));
        list.Add(Build(dir, "doc-overlay-bold", "Document", 595, 842, "같은 글을 0.4 pt 어긋나게 두 번 찍은 굵게 흉내(한 문단으로 묶여야 함)", p =>
        {
            double y = 90;
            foreach (var t in En.Take(3)) y = p.Paragraph(t, arial, 60, y, 475, 15, overlay: true) + 9;
        }));
        list.Add(Build(dir, "doc-center-right", "Document", 595, 842, "가운데 맞춤 3줄 문단 + 오른쪽 맞춤 3줄 문단 + 왼쪽 문단", p =>
        {
            double y = 100;
            y = p.Paragraph("Quality Assurance Handbook for Engine Room Piping Installation Works Rev. 3", PageBuilder.Font("Arial", 14, true), 100, y, 395, 20, 'C') + 20;
            y = p.Paragraph("Issued by the Technical Department of the shipyard for use in the building of hull number 1234 and sister vessels", arial, 250, y, 285, 15, 'R') + 20;
            p.Paragraph(En[1], arial, 60, y, 475, 15);
        }));
        list.Add(Build(dir, "doc-en-winansi", "Document", 595, 842, "단순 TrueType 글꼴(WinAnsi, /Widths, ToUnicode 없음)로 쓴 영문 본문", p =>
        {
            var f = new XFont("Arial", 11, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.WinAnsi));
            double y = 90;
            for (int i = 0; i < 3; i++) y = p.Paragraph(En[i], f, 60, y, 475, 15) + 9;
        }));
        list.Add(Build(dir, "doc-std14", "Document", 595, 842, "표준 14 글꼴(Helvetica, 비임베드, /Widths 없음) 영문 본문", p =>
        {
            AddRawFont(p.Doc, p.Page, "/HelvF", "/Helvetica", differences: false);
            var sb = new StringBuilder();
            double y = 842 - 90;
            foreach (var t in En.Take(3))
            {
                int pid = p.NewPid();
                foreach (var l in Wrap14(t, 460, 11))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"BT /HelvF 11 Tf 1 0 0 1 60 {y:0.##} Tm ({Esc(l)}) Tj ET\n");
                    p.Truth.Add(new TruthLine(l, pid));
                    y -= 15;
                }
                y -= 9;
            }
            p.Page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(sb.ToString()));
        }));
        list.Add(Build(dir, "doc-differences", "Document", 595, 842, "단순 Type1 글꼴 + /Encoding /Differences + /Widths, ToUnicode 없음", p =>
        {
            AddRawFont(p.Doc, p.Page, "/DiffF", "/Helvetica", differences: true);
            var sb = new StringBuilder();
            double y = 842 - 90;
            foreach (var t in En.Take(3))
            {
                int pid = p.NewPid();
                foreach (var l in Wrap14(t, 460, 11))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"BT /DiffF 11 Tf 1 0 0 1 60 {y:0.##} Tm ({Esc(l)}) Tj ET\n");
                    p.Truth.Add(new TruthLine(l, pid));
                    y -= 15;
                }
                y -= 9;
            }
            p.Page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(sb.ToString()));
        }));

        // ---------- 도면형 ----------
        list.Add(Build(dir, "dwg-titleblock-rules", "Drawing", 1191, 842, "표제란: 선으로 칸을 나눈 격자, 칸마다 라벨(7pt)+값(10pt, 1~2줄), 같은 글꼴 칸이 위아래로 이어진 개정란", p =>
        {
            var lab = PageBuilder.Font("Arial", 7);
            var val = PageBuilder.Font("Arial", 10);
            double x0 = 700, y0 = 600, cw = 120, ch = 38;
            string[] labels = ["TITLE", "DRAWING NO", "SCALE", "SHEET", "DRAWN", "CHECKED", "APPROVED", "DATE"];
            string[] values = ["PIPING ARRANGEMENT", "DWG-1234-0051", "1:50", "3 OF 12", "J.PARK", "S.PARK", "K.LEE", "2026-10-22"];
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                {
                    int i = r * 4 + c;
                    if (i >= 8) break;
                    double x = x0 + c * cw, y = y0 + r * ch;
                    p.Line(labels[i], lab, x + 4, y + 9, p.NewPid(), table: true);
                    p.Line(values[i], val, x + 4, y + 26, p.NewPid(), table: true);
                }
            // 두 줄 값 칸
            int two = p.NewPid();
            p.Line("SEA WATER BALLAST", val, x0 + 4, y0 + 2 * ch + 20, two, table: true);
            p.Line("PUMP ROOM PORT", val, x0 + 4, y0 + 2 * ch + 32, two, table: true);
            for (int r = 0; r <= 5; r++) p.Rule(x0, y0 + r * ch, x0 + 4 * cw, y0 + r * ch);
            for (int c = 0; c <= 4; c++) p.Rule(x0 + c * cw, y0, x0 + c * cw, y0 + 5 * ch);
            // 개정란: 같은 글꼴·크기·왼쪽 맞춤 세 칸이 18pt 간격(선 있음)
            var rev = PageBuilder.Font("Arial", 9);
            double ry = 540;
            string[] revs = ["REV A ISSUED FOR REVIEW", "REV B PIPE ROUTE CHANGED", "REV C ISSUED FOR CONSTRUCTION"];
            for (int i = 0; i < 3; i++) { p.Line(revs[i], rev, 704, ry + 13 + i * 18, p.NewPid(), table: true); }
            for (int i = 0; i <= 3; i++) p.Rule(700, ry + i * 18, 940, ry + i * 18);
            p.Rule(700, ry, 700, ry + 54); p.Rule(940, ry, 940, ry + 54);
        }));
        list.Add(Build(dir, "dwg-table-norules", "Drawing", 1191, 842, "선 없는 표: 4열 × 6행, 짧은 값(9pt), 칸 두 줄 짜리 열 포함", p =>
        {
            var f = PageBuilder.Font("Arial", 9);
            double x0 = 100, y0 = 120;
            string[][] cols =
            [
                ["TAG", "P-101", "P-102", "V-201", "V-202", "T-301"],
                ["SERVICE", "SEA WATER", "FUEL OIL", "FRESH WATER", "LUBE OIL", "BILGE"],
                ["CAPACITY", "250 M3/H", "40 M3/H", "15 M3/H", "8 M3/H", "120 M3"],
                ["REMARK", "ESSENTIAL", "STAND-BY", "ESSENTIAL", "MANUAL", "SPARE"],
            ];
            for (int c = 0; c < cols.Length; c++)
                for (int r = 0; r < 6; r++)
                    p.Line(cols[c][r] + (c == 0 ? "" : ""), f, x0 + c * 110, y0 + r * 18, p.NewPid(), table: true);
            // 두 줄 칸
            int two = p.NewPid();
            p.Line("INSULATED WITH", f, x0 + 440, y0 + 10, two, table: true);
            p.Line("MINERAL WOOL 50MM", f, x0 + 440, y0 + 21, two, table: true);
        }));
        list.Add(Build(dir, "dwg-notes", "Drawing", 1191, 842, "주기(NOTES): 번호 목록, 항목마다 1~3줄, 이어지는 줄은 들여쓰기", p =>
        {
            var n = PageBuilder.Font("Arial", 8);
            double y = 120;
            p.Line("NOTES:", PageBuilder.Font("Arial", 9, true), 80, y, p.NewPid()); y += 14;
            string[] items =
            [
                "1. ALL DIMENSIONS ARE IN MILLIMETRES UNLESS OTHERWISE STATED ON THE DRAWING.",
                "2. PIPE SUPPORTS SHALL BE LOCATED AS PER THE SUPPORT SCHEDULE AND MAY BE ADJUSTED ON SITE WITHIN 50MM.",
                "3. WELDING SHALL BE CARRIED OUT BY QUALIFIED WELDERS ACCORDING TO THE APPROVED PROCEDURE AND INSPECTED BY CLASS SURVEYOR.",
                "4. ALL VALVES SHALL BE INSTALLED WITH HAND WHEEL UPWARDS.",
                "5. FOR PIPE MATERIAL AND WALL THICKNESS REFER TO PIPING CLASS SPECIFICATION AND THE APPROVED MATERIAL LIST OF THIS HULL.",
            ];
            foreach (var it in items)
            {
                int pid = p.NewPid();
                var lines = p.Wrap(it, n, 300, 0, false);
                // 이어지는 줄 들여쓰기 12pt
                var all = new List<string>();
                var rest = it;
                var first = p.Wrap(it, n, 300, 0, false)[0];
                all.Add(first);
                rest = it[first.Length..].TrimStart();
                if (rest.Length > 0) all.AddRange(p.Wrap(rest, n, 288, 0, false));
                for (int i = 0; i < all.Count; i++)
                {
                    p.Draw(all[i], n, 80 + (i == 0 ? 0 : 12), y);
                    p.Truth.Add(new TruthLine(all[i], pid));
                    y += 10;
                }
                y += 3;
            }
        }));
        list.Add(Build(dir, "dwg-legend", "Drawing", 1191, 842, "범례: 같은 글꼴·왼쪽 맞춤 짧은 항목이 줄 간격 1.3 em으로 쌓임(항목마다 따로 — 묶으면 안 됨)", p =>
        {
            var f = PageBuilder.Font("Arial", 8);
            double y = 120;
            foreach (var t in new[] { "SEA WATER LINE", "FRESH WATER LINE", "FUEL OIL LINE", "LUBE OIL LINE", "BILGE LINE", "COMPRESSED AIR" })
            { p.Line(t, f, 90, y, p.NewPid()); y += 10.4; }
            y += 40;
            foreach (var t in new[] { "MAIN ENGINE", "AUX ENGINE NO.1", "AUX ENGINE NO.2" })
            { p.Line(t, f, 90, y, p.NewPid()); y += 14; }
        }));
        list.Add(Build(dir, "doc-toc", "Document", 595, 842, "목차: 제목(왼쪽) + 쪽 번호(오른쪽 끝)가 같은 기준선, 줄마다 따로", p =>
        {
            double y = 100;
            string[] t = ["1. Introduction", "2. Scope of Supply", "3. Piping Arrangement", "4. Valve Remote Control", "5. Commissioning", "6. Spare Parts"];
            for (int i = 0; i < t.Length; i++)
            {
                p.Line(t[i], arial, 60, y, p.NewPid());
                p.Line((3 + i * 4).ToString(), arial, 520, y, p.NewPid());
                y += 18;
            }
        }));
        list.Add(Build(dir, "dwg-rot90", "Drawing", 1191, 842, "가로 문단 3줄 + 90° 회전 문단 4줄", p =>
        {
            var f = PageBuilder.Font("Arial", 9);
            p.Paragraph("GENERAL ARRANGEMENT OF THE ENGINE ROOM SHOWING THE POSITION OF MAIN AND AUXILIARY EQUIPMENT ON THE UPPER PLATFORM LEVEL", f, 100, 150, 260, 12);
            int pid = p.NewPid();
            var lines = p.Wrap("SECTION A-A LOOKING TOWARDS THE FORWARD BULKHEAD WITH PORT SIDE EQUIPMENT REMOVED FOR CLARITY OF THE PIPE ROUTING", f, 240, 0, false);
            p.G.Save();
            p.G.TranslateTransform(700, 600);
            p.G.RotateTransform(-90);
            for (int i = 0; i < lines.Count; i++)
            {
                p.Draw(lines[i], f, 0, i * 12);
                p.Truth.Add(new TruthLine(lines[i], pid));
            }
            p.G.Restore();
        }));
        list.Add(Build(dir, "dwg-dimensions", "Drawing", 1191, 842, "치수 글 흩어짐: 짧은 글 40개를 무작위 위치에(전부 따로)", p =>
        {
            var f = PageBuilder.Font("Arial", 8);
            var rnd = new Random(20261022);
            var placed = new List<(double X, double Y)>();
            int n = 0;
            while (placed.Count < 40 && n++ < 2000)
            {
                double x = 80 + rnd.NextDouble() * 1000, y = 100 + rnd.NextDouble() * 640;
                if (placed.Any(q => Math.Abs(q.X - x) < 60 && Math.Abs(q.Y - y) < 40)) continue;
                placed.Add((x, y));
                string t = rnd.Next(4) switch { 0 => $"{rnd.Next(100, 9999)}", 1 => $"R{rnd.Next(5, 99)}", 2 => $"Ø{rnd.Next(10, 400)}", _ => $"{rnd.Next(10, 90)}.{rnd.Next(0, 9)}" };
                if (p.Truth.Any(tl => tl.Text == t)) continue;
                p.Line(t, f, x, y, p.NewPid());
            }
        }));
        list.Add(Build(dir, "dwg-form-text", "Drawing", 1191, 842, "폼 XObject 안의 글(편집 대상 아님, V15) + 쪽에 직접 쓴 글 한 줄", p =>
        {
            var f = PageBuilder.Font("Arial", 10);
            var form = new XForm(p.Doc, XUnit.FromPoint(300), XUnit.FromPoint(80));
            using (var gf = XGraphics.FromForm(form))
            {
                gf.DrawString("FORM CHILD LINE ONE", f, XBrushes.Black, new XPoint(10, 30), XStringFormats.BaseLineLeft);
                gf.DrawString("FORM CHILD LINE TWO", f, XBrushes.Black, new XPoint(10, 44), XStringFormats.BaseLineLeft);
            }
            p.G.DrawImage(form, 100, 100);
            p.Line("DIRECT PAGE TEXT LINE", f, 100, 300, p.NewPid());
        }));
        list.Add(Build(dir, "dwg-invisible-ocr", "Drawing", 595, 842, "스캔 그림 위의 보이지 않는 글(Tr 3, OCR 층) 8줄 — 편집 대상 아님", p =>
        {
            using var img = XImage.FromStream(new MemoryStream(TestImages.Png(60, 80, (x, y) => ((byte)(200 + (x % 5)), (byte)(200 + (y % 5)), 190, 255))));
            p.G.DrawImage(img, 40, 40, 515, 760);
            AddRawFont(p.Doc, p.Page, "/OcrF", "/Helvetica", differences: false);
            var sb = new StringBuilder();
            for (int i = 0; i < 8; i++)
                sb.Append(CultureInfo.InvariantCulture, $"BT 3 Tr /OcrF 11 Tf 1 0 0 1 60 {700 - i * 15} Tm (hidden scanned line number {i + 1} of the document) Tj ET\n");
            p.Page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(sb.ToString()));
        }));
        return list;
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    /// <summary>표준 Helvetica 대략 폭(1000 단위)으로 비임베드 글꼴 줄바꿈 — 정답용(PDFium도 /Widths 없는 표준 14는 내장 폭표를 쓴다; 오차는 정답 줄바꿈에만 영향).</summary>
    private static List<string> Wrap14(string text, double width, double size)
    {
        var lines = new List<string>();
        string cur = "";
        foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string cand = cur.Length == 0 ? w : cur + " " + w;
            if (cur.Length == 0 || Helv(cand) * size / 1000 <= width) cur = cand; else { lines.Add(cur); cur = w; }
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }

    /// <summary>Helvetica AFM 폭(ASCII 32~126).</summary>
    internal static readonly int[] HelveticaWidths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private static double Helv(string s) => s.Sum(c => c is >= ' ' and <= '~' ? HelveticaWidths[c - 32] : 556);

    /// <summary>쪽 /Resources /Font에 비임베드 Type1 글꼴을 넣는다. differences면 /Differences와 /Widths를 단다(ToUnicode 없음).</summary>
    private static void AddRawFont(PdfDocument doc, PdfPage page, string res, string baseFont, bool differences)
    {
        var f = new PdfDictionary(doc);
        f.Elements["/Type"] = new PdfName("/Font");
        f.Elements["/Subtype"] = new PdfName("/Type1");
        f.Elements["/BaseFont"] = new PdfName(baseFont);
        if (differences)
        {
            var enc = new PdfDictionary(doc);
            enc.Elements["/Type"] = new PdfName("/Encoding");
            enc.Elements["/BaseEncoding"] = new PdfName("/WinAnsiEncoding");
            var diff = new PdfArray(doc);
            diff.Elements.Add(new PdfInteger(45)); diff.Elements.Add(new PdfName("/hyphen"));   // 형식만 갖춘 Differences(결과 같음)
            enc.Elements["/Differences"] = diff;
            f.Elements["/Encoding"] = enc;
            f.Elements.SetInteger("/FirstChar", 32);
            f.Elements.SetInteger("/LastChar", 126);
            var w = new PdfArray(doc);
            foreach (int v in HelveticaWidths) w.Elements.Add(new PdfInteger(v));
            f.Elements["/Widths"] = w;
        }
        else f.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        doc.Internals.AddObject(f);
        var resources = page.Elements.GetDictionary("/Resources");
        if (resources is null) { resources = new PdfDictionary(doc); page.Elements["/Resources"] = resources; }
        var fonts = resources.Elements.GetDictionary("/Font");
        if (fonts is null) { fonts = new PdfDictionary(doc); resources.Elements["/Font"] = fonts; }
        fonts.Elements[res] = f.Reference!;
    }
}
