using PdfEditor.ContentStream;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Pdf.Native;
using SharpDocument = PdfSharp.Pdf.PdfDocument;

namespace PdfEditor.TextEdit.Tests;

/// <summary>한 쪽의 연구용 묶음: PDFium 읽기 + 스트림 대응(있으면) + 줄 조립 결과.</summary>
internal sealed class PageStudy
{
    public string Label = "";
    public int PageIndex;
    public PageData Pd = null!;
    public Dictionary<int, TextStream>? Streams;
    public string? StreamFail;
    public double ParseMs;
    public ParagraphProbe.Page Page = null!;
    public Dictionary<Run, TextStream> RunStream = new();
}

/// <summary>한 줄 무리(문단 후보)를 편집 없이 다시 줄바꿈해 본 결과.</summary>
internal sealed record Repro(bool Measurable, bool WordOk, bool CharOk, List<double> SlackEm, string Why, bool Korean, List<(double U0, double Nat, double Em, bool Last)>? Lines = null);

internal static class ProbeStudy
{
    public static List<PageStudy> Study(PdfiumThread t, string label, string path, int maxPages, int firstPage = 0)
    {
        var list = new List<PageStudy>();
        SharpDocument doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        int pages = Math.Min(doc.PageCount, firstPage + maxPages);
        for (int p = firstPage; p < pages; p++)
        {
            var pd = ParagraphProbe.ReadPdfium(t, path, p);
            var ps = new PageStudy { Label = label, PageIndex = p, Pd = pd };
            ps.Streams = StreamMap.Build(doc.Pages[p], pd.Kinds, out ps.StreamFail, out ps.ParseMs, out _);
            var segs = ParagraphProbe.Assemble(pd.Runs, pd.Obstacles);
            ps.Page = new ParagraphProbe.Page(segs, pd.Obstacles);
            if (ps.Streams is not null) foreach (var r in pd.Runs) if (ps.Streams.TryGetValue(r.Obj, out var ts)) ps.RunStream[r] = ts;
            list.Add(ps);
        }
        return list;
    }

    // ---------------------------------------------------------------- 코드북 캐시
    private sealed class RefComparer : IEqualityComparer<PdfDictionary>
    {
        public bool Equals(PdfDictionary? a, PdfDictionary? b) => ReferenceEquals(a, b);
        public int GetHashCode(PdfDictionary o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
    }

    internal sealed class Books
    {
        private readonly Dictionary<PdfDictionary, (Book?, string)> _strict = new(new RefComparer());
        private readonly Dictionary<PdfDictionary, (Book?, string)> _relaxed = new(new RefComparer());
        public static (Book? Book, string Reason) Fresh(PdfDictionary f) => BookBuilder.Build(f, false);
        public (Book? Book, string Reason) Get(PdfDictionary f, bool relaxed)
        {
            var cache = relaxed ? _relaxed : _strict;
            if (!cache.TryGetValue(f, out var v)) cache[f] = v = BookBuilder.Build(f, relaxed);
            return v;
        }
    }

    // ---------------------------------------------------------------- 무편집 재배치 (V17 실제 PDF / V19)
    /// <summary>
    /// 문단 후보 g의 줄들을 한 덩어리 글로 합쳐 같은 폭에서 다시 줄바꿈했을 때 원래 줄바꿈과 같은가.
    /// 폭은 코드북(원래 글꼴 폭표 + 자간·가로 비율)으로 잰다. 코드북을 못 만들면 Measurable = false.
    /// WordOk = 낱말(공백) 단위 줄바꿈 가정으로 같음, CharOk = 글자 단위 줄바꿈 가정으로 같음.
    /// </summary>
    public static Repro Reproduce(PageStudy ps, Group g, Books books, bool relaxed)
    {
        if (ps.Streams is null) return new Repro(false, false, false, [], "no stream map", false);
        var lines = g.Lines;
        // 글자 폭 표(쪽 단위, 이 문단의 글 객체에서 실제 쓰인 글자 + 코드북 역변환)
        var w = new Dictionary<char, double>();
        double space = -1;
        foreach (var seg in lines)
            foreach (var run in seg.Runs)
            {
                if (!ps.RunStream.TryGetValue(run, out var ts)) return new Repro(false, false, false, [], "run without stream", false);
                var (book, why) = books.Get(ts.Font, relaxed);
                if (book is null) return new Repro(false, false, false, [], "no codebook: " + why, false);
                double k = run.Scale * ts.Tz / 100.0;
                foreach (var (bytes, _) in ts.Pieces)
                {
                    if (bytes is null) continue;
                    foreach (int code in book.Codes(bytes))
                    {
                        string s = book.ToUni.TryGetValue(code, out var u) ? u : (relaxed && code is >= 32 and < 256 ? ((char)code).ToString() : "");
                        if (s.Length != 1) continue;
                        double adv = k * (ts.Tf * book.Width(code) / 1000.0 + ts.Tc + (!book.TwoByte && code == 32 ? ts.Tw : 0));
                        if (s[0] == ' ') space = adv; else w.TryAdd(s[0], adv);
                    }
                }
                // 이 글꼴의 공백 폭(글자로 안 쓰였어도): 역변환 → 코드 → 폭
                if (space < 0 && book.CharToCode.TryGetValue(' ', out int sc)) space = k * (ts.Tf * book.Width(sc) / 1000.0 + ts.Tc);
                if (space < 0 && !book.TwoByte) space = k * (ts.Tf * book.Width(32) / 1000.0 + ts.Tc + ts.Tw);
                foreach (var ch in book.CharToCode.Keys)
                    if (ch != ' ' && !w.ContainsKey(ch) && lines.Any(l => l.Text.Contains(ch))) w[ch] = k * (ts.Tf * book.Width(book.CharToCode[ch]) / 1000.0 + ts.Tc);
            }
        if (space < 0) space = lines[0].Size * 0.25;   // 모르면 0.25 em(가정)
        var texts = lines.Select(l => l.Text).ToList();
        foreach (var tx in texts) foreach (char c in tx) if (c != ' ' && !w.ContainsKey(c)) return new Repro(false, false, false, [], $"width unknown for U+{(int)c:X4}", false);
        double Wd(string s) => s.Sum(c => c == ' ' ? space : w[c]);

        bool korean = texts.Sum(tx => tx.Count(c => c is >= '가' and <= '힣')) * 2 > texts.Sum(tx => tx.Count(char.IsLetter));
        var align = g.Align;
        double em = lines[0].Size;
        double left = lines.Count >= 2 ? ParagraphProbe.Median(lines.Skip(1).Select(l => l.U0)) : lines[0].U0;
        double indent = lines[0].U0 - left;
        double rbox = lines.Max(l => Math.Max(l.U1, l.U0 + Wd(l.Text)));   // 잉크 오른쪽 끝과 글자 폭(오른쪽 여백 포함) 중 먼 쪽
        double boxW = align is Align.Left or Align.Justified ? rbox - left : lines.Max(l => l.Width);
        double eps = 0.5 + 0.0015 * boxW;
        double Avail(int i) => align is Align.Left or Align.Justified ? boxW - (i == 0 ? indent : 0) : boxW;

        var slack = new List<double>();
        for (int i = 0; i + 1 < lines.Count; i++) slack.Add((Avail(i) - Wd(texts[i])) / em);

        // 낱말 단위
        var words = texts.SelectMany(tx => tx.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => (x, i: 0))).ToList();
        var orig = texts.Select(tx => tx.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length).ToList();
        var wl = new List<int>(); int cnt = 0; double cur = 0; int li = 0;
        foreach (var (x, _) in words)
        {
            double ww = Wd(x);
            double next = cnt == 0 ? ww : cur + space + ww;
            if (cnt > 0 && next > Avail(li) + eps) { wl.Add(cnt); li++; cnt = 1; cur = ww; }
            else { cnt++; cur = next; }
        }
        wl.Add(cnt);
        bool wordOk = wl.SequenceEqual(orig);

        // 글자 단위(공백 포함 한 줄 글자열을 이어 붙임)
        var all = string.Concat(texts);
        var origLen = texts.Select(tx => tx.Count(c => c != ' ')).ToList();
        var cl = new List<int>(); cnt = 0; cur = 0; li = 0;
        foreach (char c in all)
        {
            double cw = c == ' ' ? space : w[c];
            if (c == ' ' && cnt == 0) continue;                       // 줄 머리의 공백은 버린다
            if (cnt > 0 && c != ' ' && cur + cw > Avail(li) + eps) { cl.Add(cnt); li++; cnt = 1; cur = cw; }
            else { if (c != ' ') cnt++; cur += cw; }
        }
        cl.Add(cnt);
        bool charOk = cl.SequenceEqual(origLen);
            var lineInfo = lines.Select((l, i) => (l.U0, Wd(texts[i]), em, i == lines.Count - 1)).ToList();
    return new Repro(true, wordOk, charOk, slack, wordOk || charOk ? "" : $"words orig [{string.Join(",", orig)}] re [{string.Join(",", wl)}] | chars orig [{string.Join(",", origLen)}] re [{string.Join(",", cl)}] boxW={boxW:0.#} eps={eps:0.#}", korean, lineInfo);
    }
}
