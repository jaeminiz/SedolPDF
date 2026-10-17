using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfDoc = PdfSharp.Pdf.PdfDocument;

namespace PdfEditor.Exchange.Summary;

/// <summary>
/// 요약 PDF에 들어가는 고정 문구 틀. 화면 문구는 앱의 리소스 파일에 있고 여기로 넘겨 받는다(이 모듈에는 화면 문구가 없다).
/// 틀의 {0}·{1}은 string.Format 자리 표시자다.
/// </summary>
public sealed record SummaryText(
    string CreatedFormat,                // {0} = 만든 시각
    string CountFormat,                  // {0} = 주석 수
    string PageHeadingFormat,            // {0} = 원본 쪽 번호 (1부터)
    string PageHeadingContinuedFormat,   // {0} = 원본 쪽 번호 — 요약이 다음 쪽으로 이어질 때 반복하는 쪽 머리
    string MissingGlyphsNoticeFormat)    // {0} = 개수, {1} = 코드 목록 — 글꼴에 없는 글자 알림
{
    /// <summary>문구를 넘기지 않았을 때의 영어 기본값 (앱은 항상 리소스에서 만든 틀을 넘긴다).</summary>
    public static SummaryText English { get; } = new(
        "Created: {0}", "{0} annotations", "Page {0}", "Page {0} (continued)", "{0} characters are not in the font: {1}");
}

/// <summary>
/// 요약에 넣을 주석 한 건. Number는 최상위 주석의 번호(문서 전체에서 이어지는 번호, 답글은 0 = 번호 없음).
/// MarkRect는 쪽 그림 위의 위치로 그림 픽셀 좌표(왼쪽 위 (0,0), y 아래쪽)이다.
/// </summary>
public sealed record SummaryEntry(
    int Number, string KindName, string? Author, DateTimeOffset? When, string? Contents,
    IReadOnlyList<SummaryEntry> Replies, PdfRectF MarkRect);

/// <summary>주석이 있는 쪽 하나. PageImageBgra는 ImageW×ImageH×4 바이트(BGRA, 위에서 아래로).</summary>
public sealed record SummaryPage(int PageNumber, byte[] PageImageBgra, int ImageW, int ImageH, IReadOnlyList<SummaryEntry> Entries);

public sealed record SummaryInput(string DocumentName, DateTimeOffset CreatedAt, IReadOnlyList<SummaryPage> Pages)
{
    public SummaryText Text { get; init; } = SummaryText.English;
}

/// <summary>요약을 만든 결과. MissingCodepoints는 글꼴에 없어 그리지 못한 글자(끝 쪽에도 알림 문구로 적는다).</summary>
public sealed record SummaryBuildResult(int SummaryPageCount, int EntryCount, IReadOnlyList<int> MissingCodepoints);

/// <summary>
/// 주석 요약 PDF 만들기 (M3b B7). A4 세로. 첫 쪽 머리말(문서 이름·만든 시각·주석 수) 다음에,
/// 주석이 있는 쪽마다 왼쪽에 쪽 그림(폭 40%, 번호 표시 포함), 오른쪽에 번호·종류·작성자·시각·내용·답글 목록을 놓는다.
/// 긴 내용은 줄바꿈하고 쪽 아래에 닿으면 다음 요약 쪽에 같은 쪽 머리(이어서)로 계속한다. 주석이 없는 쪽은 건너뛴다.
/// 글자는 Noto Sans KR(FontEmbedder + TextLayout)이고, PDFsharp의 글꼴 그리기는 쓰지 않고 내용 스트림을 직접 쓴다.
/// 임시 파일에 쓴 뒤 옮기므로 취소·실패 때 출력 파일(과 이미 있던 파일)이 건드려지지 않는다.
/// </summary>
public static class SummaryPdfBuilder
{
    private const float PageW = 595.28f, PageH = 841.89f, Margin = 40f;
    private const float ContentW = PageW - 2 * Margin;
    private const float ImageColW = ContentW * 0.4f, ColGap = 14f;
    private const float ColX = Margin + ImageColW + ColGap, ColW = PageW - Margin - ColX;
    private const float ReplyIndent = 14f, EntryGap = 6f, SectionGap = 14f;
    private const float TitleSize = 18f, SubSize = 10f, HeadingSize = 12f, MetaSize = 8.5f, BodySize = 9.5f;
    private const float LineFactor = 1.2f;

    private static readonly PdfColor Black = new(0, 0, 0), Gray = new(90, 90, 90), Red = new(200, 30, 30), White = new(255, 255, 255);

    public static SummaryBuildResult Build(SummaryInput input, AppFont font, string outputPath, CancellationToken ct,
        IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ct.ThrowIfCancellationRequested();

        string tmp = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var flow = new Flow(input, font, ct);
            var result = flow.Run(progress);
            ct.ThrowIfCancellationRequested();
            flow.Doc.Save(tmp);
            ct.ThrowIfCancellationRequested();
            File.Move(tmp, outputPath, overwrite: true);
            return result;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>요약 PDF 한 쪽의 내용 스트림과 쓰는 이미지.</summary>
    private sealed class Canvas
    {
        public readonly StringBuilder Content = new();
        public readonly List<(string Name, PdfReference Ref)> Images = [];
    }

    private sealed class Flow
    {
        private readonly SummaryInput _in;
        private readonly AppFont _font;
        private readonly CancellationToken _ct;
        private readonly List<Canvas> _pages = [];
        private readonly SortedSet<int> _missing = [];
        private readonly SortedSet<int> _codepoints = [];
        private readonly Dictionary<(int Cid, string Text), GlyphUse> _glyphs = [];
        private readonly SummaryText _t;
        private Canvas _cur = null!;
        private float _y;                // 다음 줄의 위쪽
        private int _sourcePage;
        private int _imageSeq;

        public PdfDoc Doc { get; } = new();

        public Flow(SummaryInput input, AppFont font, CancellationToken ct)
        {
            _in = input;
            _font = font;
            _ct = ct;
            _t = input.Text ?? SummaryText.English;
        }

        private static float Bottom => Margin;

        public SummaryBuildResult Run(IProgress<int>? progress)
        {
            Doc.Info.Title = _in.DocumentName;
            Doc.Info.Creator = "SedolPDF";

            var pages = _in.Pages.Where(p => p.Entries.Count > 0).ToList();
            int total = pages.Sum(p => Count(p.Entries));

            NewPage();
            DrawHeader(total);
            int done = 0;
            foreach (var sp in pages)
            {
                _ct.ThrowIfCancellationRequested();
                DrawSourcePage(sp);
                progress?.Report(++done);
            }
            DrawMissingNotice();
            FinishDocument();
            return new SummaryBuildResult(_pages.Count, total, [.. _missing]);
        }

        private static int Count(IReadOnlyList<SummaryEntry> es) => es.Sum(e => 1 + Count(e.Replies));

        // ---- 쪽 흐름 ----

        private void NewPage()
        {
            _cur = new Canvas();
            _pages.Add(_cur);
            _y = PageH - Margin;
        }

        private void EnsureSpace(float height)
        {
            if (_y - height >= Bottom) return;
            NewPage();
            if (_sourcePage > 0) DrawHeading(string.Format(CultureInfo.CurrentCulture, _t.PageHeadingContinuedFormat, _sourcePage));
        }

        // ---- 글자 ----

        private LaidOutText Lay(string text, float size, float width)
        {
            var lt = TextLayout.Layout(_font, text, size, width, TextAlign.Left, new PdfPointF(0, 0), LineFactor);
            foreach (int m in lt.MissingCodepoints) _missing.Add(m);
            return lt;
        }

        /// <summary>배치된 글을 한 줄씩 (x, 현재 위치)에 쓴다. 쪽 아래에 닿으면 다음 쪽(쪽 머리 반복)으로 넘어간다.</summary>
        private void Emit(LaidOutText lt, float size, PdfColor color, float x)
        {
            float lh = size * LineFactor;
            for (int i = 0; i < lt.Lines.Length; i++)
            {
                EnsureSpace(lh);
                var line = lt.Lines[i];
                if (line.Length > 0) WriteGlyphs(line, i, lh, size, color, x);
                _y -= lh;
            }
        }

        private void WriteGlyphs(ImmutableArray<PositionedGlyph> line, int lineIndex, float lh, float size, PdfColor color, float x)
        {
            float gy = _y + line[0].Origin.Y + lineIndex * lh;   // Origin.Y는 첫 줄 위쪽(0) 기준 기준선(음수)
            AppendTextLine(_cur.Content, line, x, gy, size, color);
        }

        /// <summary>
        /// 한 줄을 Tm 한 번 + TJ 배열 하나로 쓴다. 글리프 사이의 어긋남(커닝)은 TJ 조정값으로 맞춘다.
        /// 글자마다 Tm을 쓰면 PDF 뷰어의 글자 추출이 글자마다 줄을 바꾸므로 한 줄을 한 덩어리로 쓴다.
        /// </summary>
        private void AppendTextLine(StringBuilder sb, ImmutableArray<PositionedGlyph> line, float x, float y, float size, PdfColor color)
        {
            sb.Append("BT\n/F1 ").Append(Num(size)).Append(" Tf\n").Append(Rgb(color)).Append(" rg\n")
              .Append("1 0 0 1 ").Append(Num(x + line[0].Origin.X)).Append(' ').Append(Num(y)).Append(" Tm\n[");
            for (int i = 0; i < line.Length; i++)
            {
                var g = line[i];
                sb.Append('<').Append(g.Cid.ToString("X4")).Append('>');
                Use(g, size);
                if (i + 1 < line.Length)
                {
                    float delta = line[i + 1].Origin.X - g.Origin.X;
                    float nominal = (float)Math.Round(g.Advance * 1000f / size) * size / 1000f;   // /W에 적은 폭
                    float adj = (nominal - delta) * 1000f / size;
                    if (Math.Abs(adj) >= 0.05f) sb.Append(Num(adj));
                }
            }
            sb.Append("] TJ\nET\n");
        }

        private void Use(PositionedGlyph g, float size)
        {
            foreach (var r in g.Text.EnumerateRunes()) _codepoints.Add(r.Value);
            if (_glyphs.ContainsKey((g.Cid, g.Text))) return;
            _glyphs[(g.Cid, g.Text)] = new GlyphUse(g.Cid, size > 0 ? (int)Math.Round(g.Advance * 1000f / size) : 0, g.Text);
        }

        // ---- 머리말·쪽 머리 ----

        private void DrawHeader(int total)
        {
            Emit(Lay(_in.DocumentName, TitleSize, ContentW), TitleSize, Black, Margin);
            _y -= 2;
            string created = string.Format(CultureInfo.CurrentCulture, _t.CreatedFormat,
                _in.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            Emit(Lay(created, SubSize, ContentW), SubSize, Gray, Margin);
            Emit(Lay(string.Format(CultureInfo.CurrentCulture, _t.CountFormat, total), SubSize, ContentW), SubSize, Gray, Margin);
            _y -= SectionGap;
            Rule(_y + SectionGap / 2);
        }

        private void Rule(float y)
        {
            _cur.Content.Append("q 0.8 G 0.5 w ").Append(Num(Margin)).Append(' ').Append(Num(y)).Append(" m ")
                .Append(Num(PageW - Margin)).Append(' ').Append(Num(y)).Append(" l S Q\n");
        }

        private void DrawHeading(string text)
        {
            Emit(Lay(text, HeadingSize, ContentW), HeadingSize, Black, Margin);
            _y -= 3;
        }

        // ---- 원본 쪽 하나 ----

        private void DrawSourcePage(SummaryPage sp)
        {
            if (sp.ImageW <= 0 || sp.ImageH <= 0 || sp.PageImageBgra.Length != sp.ImageW * sp.ImageH * 4)
                throw new ArgumentException($"Page {sp.PageNumber}: image buffer does not match {sp.ImageW}x{sp.ImageH} BGRA.");

            float headingH = HeadingSize * LineFactor + 3;
            float maxImgH = PageH - 2 * Margin - headingH;
            float scale = Math.Min(ImageColW / sp.ImageW, maxImgH / sp.ImageH);
            float drawW = sp.ImageW * scale, drawH = sp.ImageH * scale;

            // 쪽 머리와 그림이 지금 쪽에 들어가지 않으면 새 쪽에서 시작
            if (_y - headingH - drawH < Bottom) NewPage();
            _sourcePage = sp.PageNumber;
            DrawHeading(string.Format(CultureInfo.CurrentCulture, _t.PageHeadingFormat, sp.PageNumber));

            float top = _y;
            DrawImage(sp, Margin, top, drawW, drawH, scale);

            var canvasAtStart = _cur;
            foreach (var e in sp.Entries) DrawEntry(e, 0);

            // 다음 원본 쪽은 그림과 목록 중 더 아래까지 간 곳 다음에서 시작 (목록이 다음 쪽으로 넘어갔으면 목록 끝에서)
            float bottom = ReferenceEquals(canvasAtStart, _cur) ? Math.Min(_y, top - drawH) : _y;
            _y = bottom - SectionGap;
            _sourcePage = 0;
        }

        private void DrawImage(SummaryPage sp, float x, float top, float drawW, float drawH, float scale)
        {
            _ct.ThrowIfCancellationRequested();
            var rgb = new byte[sp.ImageW * sp.ImageH * 3];
            var src = sp.PageImageBgra;
            for (int i = 0, j = 0; i < src.Length; i += 4, j += 3)
            {
                rgb[j] = src[i + 2]; rgb[j + 1] = src[i + 1]; rgb[j + 2] = src[i];
            }
            var img = new PdfDictionary(Doc);
            img.Elements["/Type"] = new PdfName("/XObject");
            img.Elements["/Subtype"] = new PdfName("/Image");
            img.Elements["/Width"] = new PdfInteger(sp.ImageW);
            img.Elements["/Height"] = new PdfInteger(sp.ImageH);
            img.Elements["/ColorSpace"] = new PdfName("/DeviceRGB");
            img.Elements["/BitsPerComponent"] = new PdfInteger(8);
            img.CreateStream(PdfSharp.Pdf.Filters.Filtering.FlateDecode.Encode(rgb, PdfFlateEncodeMode.Default));
            img.Elements["/Filter"] = new PdfName("/FlateDecode");
            Doc.Internals.AddObject(img);

            string name = "Im" + (++_imageSeq).ToString(CultureInfo.InvariantCulture);
            _cur.Images.Add((name, img.Reference!));
            float bottom = top - drawH;
            var sb = _cur.Content;
            sb.Append("q ").Append(Num(drawW)).Append(" 0 0 ").Append(Num(drawH)).Append(' ').Append(Num(x)).Append(' ').Append(Num(bottom))
              .Append(" cm /").Append(name).Append(" Do Q\n");
            sb.Append("q 0.6 G 0.5 w ").Append(Num(x)).Append(' ').Append(Num(bottom)).Append(' ').Append(Num(drawW)).Append(' ').Append(Num(drawH)).Append(" re S Q\n");

            // 번호 표시: 최상위 주석의 MarkRect에 테두리와 번호 배지
            foreach (var e in sp.Entries)
            {
                if (e.Number <= 0) continue;
                float l = x + Math.Clamp(e.MarkRect.Left, 0, sp.ImageW) * scale, r = x + Math.Clamp(e.MarkRect.Right, 0, sp.ImageW) * scale;
                float yMinPx = Math.Min(e.MarkRect.Bottom, e.MarkRect.Top), yMaxPx = Math.Max(e.MarkRect.Bottom, e.MarkRect.Top);   // 그림 좌표는 y가 아래로 커진다
                float tp = top - Math.Clamp(yMinPx, 0, sp.ImageH) * scale;
                float bt = top - Math.Clamp(yMaxPx, 0, sp.ImageH) * scale;
                DrawMark(e.Number, l, bt, r, tp, x, top, drawW, drawH);
            }
        }

        /// <summary>그림 안(l, bt)-(r, tp)에 붉은 테두리와 왼쪽 위 번호 배지를 그린다. 모두 PDF 사용자 좌표.</summary>
        private void DrawMark(int number, float l, float bt, float r, float tp, float imgX, float imgTop, float imgW, float imgH)
        {
            var sb = _cur.Content;
            sb.Append("q 1 0 0 RG 0.8 w ").Append(Num(l)).Append(' ').Append(Num(bt)).Append(' ')
              .Append(Num(Math.Max(r - l, 1))).Append(' ').Append(Num(Math.Max(tp - bt, 1))).Append(" re S Q\n");

            const float size = 7f;
            var lt = Lay(number.ToString(CultureInfo.InvariantCulture), size, float.PositiveInfinity);
            float tw = lt.Bounds.Width;
            float bw = tw + 4, bh = size * LineFactor + 1;
            float bx = Math.Min(l, imgX + imgW - bw);
            float by = tp;                                   // 테두리 위쪽 바로 위
            if (by + bh > imgTop) by = tp - bh;              // 그림 위로 벗어나면 안쪽으로
            by = Math.Max(by, imgTop - imgH);
            sb.Append("q ").Append(Rgb(Red)).Append(" rg ").Append(Num(bx)).Append(' ').Append(Num(by)).Append(' ')
              .Append(Num(bw)).Append(' ').Append(Num(bh)).Append(" re f Q\n");

            var line = lt.Lines.Length > 0 ? lt.Lines[0] : default;
            if (line.IsDefaultOrEmpty) return;
            AppendTextLine(sb, line, bx + 2, by + bh - 0.5f + line[0].Origin.Y, size, White);
        }

        // ---- 목록 ----

        private void DrawEntry(SummaryEntry e, int depth)
        {
            _ct.ThrowIfCancellationRequested();
            float x = ColX + depth * ReplyIndent;
            float w = Math.Max(40f, ColW - depth * ReplyIndent);

            string head = e.Number > 0 ? e.Number.ToString(CultureInfo.InvariantCulture) + "  " + e.KindName : e.KindName;
            var headLt = Lay(head, BodySize, w);
            string meta = string.Join("  ·  ", new[] { e.Author, e.When?.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            var metaLt = meta.Length > 0 ? Lay(meta, MetaSize, w) : null;
            var bodyLt = string.IsNullOrWhiteSpace(e.Contents) ? null : Lay(e.Contents!, BodySize, w);

            // 머리 줄·작성자 줄과 내용 첫 줄은 같은 쪽에 놓는다
            float keep = headLt.Lines.Length * BodySize * LineFactor + (metaLt?.Lines.Length ?? 0) * MetaSize * LineFactor
                + (bodyLt is null ? 0 : BodySize * LineFactor);
            EnsureSpace(keep);

            Emit(headLt, BodySize, Black, x);
            if (metaLt is not null) Emit(metaLt, MetaSize, Gray, x);
            if (bodyLt is not null) Emit(bodyLt, BodySize, Black, x);
            _y -= EntryGap;
            foreach (var r in e.Replies) DrawEntry(r, depth + 1);
        }

        private void DrawMissingNotice()
        {
            if (_missing.Count == 0) return;
            _y -= SectionGap;
            string list = string.Join(", ", _missing.Select(c => "U+" + c.ToString("X4", CultureInfo.InvariantCulture)));
            string notice = string.Format(CultureInfo.CurrentCulture, _t.MissingGlyphsNoticeFormat, _missing.Count, list);
            _sourcePage = 0;
            Emit(Lay(notice, MetaSize, ContentW), MetaSize, Red, Margin);
        }

        // ---- 마무리: 글꼴을 한 번 넣고 쪽 만들기 ----

        private void FinishDocument()
        {
            _ct.ThrowIfCancellationRequested();
            var use = new FontUse([.. _codepoints], [.. _glyphs.Values]);
            var fontRef = FontEmbedder.Embed(Doc, _font, use);

            foreach (var c in _pages)
            {
                var page = Doc.AddPage();
                page.Width = XUnit.FromPoint(PageW);
                page.Height = XUnit.FromPoint(PageH);

                var res = new PdfDictionary(Doc);
                var fonts = new PdfDictionary(Doc);
                fonts.Elements["/F1"] = fontRef;
                res.Elements["/Font"] = fonts;
                if (c.Images.Count > 0)
                {
                    var xo = new PdfDictionary(Doc);
                    foreach (var (name, r) in c.Images) xo.Elements["/" + name] = r;
                    res.Elements["/XObject"] = xo;
                }
                page.Elements["/Resources"] = res;

                var stream = new PdfDictionary(Doc);
                stream.CreateStream(Encoding.Latin1.GetBytes(c.Content.ToString()));
                Doc.Internals.AddObject(stream);
                page.Elements["/Contents"] = stream.Reference!;
            }
        }
    }

    private static string Rgb(PdfColor c) => Num(c.R / 255f) + " " + Num(c.G / 255f) + " " + Num(c.B / 255f);

    private static string Num(float v)
    {
        var s = v.ToString("0.###", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}
