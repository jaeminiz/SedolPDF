using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Exchange.Xfdf;
using PdfEditor.TestFixtures;

namespace PdfEditor.Exchange.Tests;

/// <summary>XFDF 가져오기 강화(M3b Task 6 수정 1회): 깊은 중첩, 첨부 폭탄, 선형 답글 사슬, 거대한 좌표 목록.</summary>
public class XfdfHardeningTests : IDisposable
{
    private static long _next;
    private static AnnotId NewId() => new(Interlocked.Increment(ref _next));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-xfdfh-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly List<DocumentSession> _sessions = [];

    public XfdfHardeningTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        foreach (var s in _sessions) s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static XfdfReadResult Read(string xml, AnnotationAssets? assets = null)
        => XfdfReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml)), NewId, assets ?? new AnnotationAssets());

    private static string Wrap(string annots) => $"<?xml version=\"1.0\"?><xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots>{annots}</annots></xfdf>";

    private async Task<DocumentSession> OpenBlankAsync(string name = "b.pdf")
    {
        string p = Path.Combine(_dir, name);
        TestPdfFactory.CreateBlank(p, 1, 300, 300);
        var s = await DocumentSession.OpenAsync(_t, p, null);
        _sessions.Add(s);
        await s.AnnotationsReady;
        return s;
    }

    [Fact]
    public void DeepNesting_InsideAnnotation_SkippedWithoutCrash()
    {
        const int Depth = 200_000;
        var sb = new StringBuilder();
        sb.Append("<text page=\"0\" rect=\"0,0,10,10\" name=\"deep\"><contents>");
        for (int i = 0; i < Depth; i++) sb.Append("<a>");
        sb.Append('x');
        for (int i = 0; i < Depth; i++) sb.Append("</a>");
        sb.Append("</contents></text><square page=\"0\" rect=\"0,0,10,10\" name=\"ok\"/>");
        var r = Read(Wrap(sb.ToString()));
        Assert.Equal("ok", Assert.Single(r.Annotations).A.Name);
        Assert.Contains(r.Skipped, s => s.Contains("nested too deeply"));
    }

    [Fact]
    public void DeepNesting_AtRoot_NoCrash()
    {
        const int Depth = 200_000;
        var sb = new StringBuilder("<xfdf><zzz>");
        for (int i = 0; i < Depth; i++) sb.Append("<a>");
        for (int i = 0; i < Depth; i++) sb.Append("</a>");
        sb.Append("</zzz><annots><square page=\"0\" rect=\"0,0,1,1\" name=\"ok\"/></annots></xfdf>");
        var r = Read(sb.ToString());
        Assert.Single(r.Annotations);
    }

    [Fact]
    public void DeepNesting_InGesture_NoCrash()
    {
        var sb = new StringBuilder();
        sb.Append("<ink page=\"0\" rect=\"0,0,10,10\" name=\"i\"><inklist><gesture>1,1;2,2");
        for (int i = 0; i < 100_000; i++) sb.Append("<b>");
        for (int i = 0; i < 100_000; i++) sb.Append("</b>");
        sb.Append("</gesture></inklist></ink>");
        var r = Read(Wrap(sb.ToString()));
        Assert.Empty(r.Annotations);
        Assert.Single(r.Skipped);
    }

    [Fact]
    public void ManySmallCompressedAttachments_TotalCapped_AndNotStoredByRead()
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, true))
        {
            var zeros = new byte[1024 * 1024];
            for (int i = 0; i < 20; i++) z.Write(zeros);   // 20 MB로 풀림(파일 상한 25 MB 안)
        }
        string b64 = Convert.ToBase64String(ms.ToArray());
        var sb = new StringBuilder();
        for (int i = 0; i < 14; i++)   // 14 x 20 MB = 280 MB > 200 MB
            sb.Append($"<fileattachment page=\"0\" rect=\"0,0,10,10\" file=\"f{i}.bin\" name=\"f{i}\"><data encoding=\"base64\" filter=\"FlateDecode\">{b64}</data></fileattachment>");
        var r = Read(Wrap(sb.ToString()));
        Assert.Equal(10, r.Annotations.Count);
        Assert.Equal(4, r.Skipped.Count(s => s.Contains("total limit")));
        Assert.All(r.Annotations, x => Assert.Null(x.A.AttachmentId));   // 읽기만으로는 파일 저장소에 넣지 않는다
    }

    private const string Att = "<fileattachment page=\"{0}\" rect=\"0,0,10,10\" file=\"{1}\" name=\"{1}\"><data encoding=\"base64\">SGVsbG8=</data></fileattachment>";

    [Fact]
    public async Task Attachments_StoredOnlyForKeptAnnotations()
    {
        string xml = Wrap(string.Format(Att, 0, "a.txt") + string.Format(Att, 5, "b.txt"));

        // 암호 문서: 첨부는 M2 유형이 아니므로 하나도 저장소에 들어가지 않는다.
        string p = Path.Combine(_dir, "enc.pdf");
        TestPdfFactory.CreateEncrypted(p, 1, 300, 300, "secret");
        var enc = await DocumentSession.OpenAsync(_t, p, "secret");
        _sessions.Add(enc);
        await enc.AnnotationsReady;
        var assets = new AnnotationAssets();
        var plan = XfdfImport.Plan(enc, Read(xml, assets));
        Assert.Equal(0, plan.Imported);
        Assert.False(assets.HasFile(new AnnotationAssets().AddFile("a.txt", Encoding.ASCII.GetBytes("Hello"))));

        // 일반 문서: 쪽 번호가 맞는 것(a)만 저장소에 들어간다.
        var open = await OpenBlankAsync("open.pdf");
        var assets2 = new AnnotationAssets();
        var plan2 = XfdfImport.Plan(open, Read(xml, assets2));
        Assert.Equal(1, plan2.Imported);
        await open.Commands.ExecuteAsync(plan2.Command!);
        var kept = open.Annotations.Get(open.Pages[0].Id).Items.Single().Data;
        Assert.Equal("Hello", Encoding.ASCII.GetString(assets2.GetFile(kept.AttachmentId!).Data));
        Assert.False(assets2.HasFile(new AnnotationAssets().AddFile("b.txt", Encoding.ASCII.GetBytes("Hello"))));
    }

    [Fact]
    public async Task ReplyChain100k_PlanIsFast()
    {
        var s = await OpenBlankAsync();
        const int N = 100_000;
        var sb = new StringBuilder(N * 80);
        sb.Append("<text page=\"0\" rect=\"0,0,10,10\" name=\"n0\"/>");
        for (int i = 1; i < N; i++) sb.Append("<text page=\"0\" rect=\"0,0,10,10\" name=\"n").Append(i).Append("\" inreplyto=\"n").Append(i - 1).Append("\"/>");
        var r = Read(Wrap(sb.ToString()));
        var sw = Stopwatch.StartNew();
        var plan = XfdfImport.Plan(s, r);
        sw.Stop();
        Assert.Equal(N, plan.Imported);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Plan took {sw.Elapsed}");
        Assert.Contains(plan.Notes, n => n.Contains("too deep"));
    }

    [Fact]
    public async Task CircularChain100k_PlanIsFast()
    {
        var s = await OpenBlankAsync();
        const int N = 100_000;
        var sb = new StringBuilder();
        for (int i = 0; i < N; i++) sb.Append("<text page=\"0\" rect=\"0,0,10,10\" name=\"n").Append(i).Append("\" inreplyto=\"n").Append((i + 1) % N).Append("\"/>");
        var r = Read(Wrap(sb.ToString()));
        var sw = Stopwatch.StartNew();
        var plan = XfdfImport.Plan(s, r);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Plan took {sw.Elapsed}");
        Assert.Equal(N, plan.Imported);
    }

    /// <summary>합성 스트림: 거대한 coords 속성 하나(메모리에 쌓아 두지 않는다).</summary>
    private sealed class HugeCoordsStream(long chars) : Stream
    {
        private readonly byte[] _head = Encoding.UTF8.GetBytes("<xfdf><annots><highlight page=\"0\" rect=\"0,0,1,1\" name=\"h\" coords=\"");
        private readonly byte[] _tail = Encoding.UTF8.GetBytes("\"/><square page=\"0\" rect=\"0,0,1,1\" name=\"ok\"/></annots></xfdf>");
        private long _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = 0;
            long total = _head.Length + chars + _tail.Length;
            while (n < count && _pos < total)
            {
                byte b;
                if (_pos < _head.Length) b = _head[_pos];
                else if (_pos < _head.Length + chars) b = ((_pos - _head.Length) & 1) == 0 ? (byte)'1' : (byte)',';
                else b = _tail[_pos - _head.Length - chars];
                buffer[offset + n++] = b;
                _pos++;
            }
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void HugeCoordsAttribute_SkippedWithoutGigabytes()
    {
        long before = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        var r = XfdfReader.Read(new HugeCoordsStream(90_000_000), NewId, new AnnotationAssets());
        sw.Stop();
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        Assert.Equal("ok", Assert.Single(r.Annotations).A.Name);
        Assert.Contains(r.Skipped, s => s.Contains("too long"));
        Assert.True(allocated < 1_000_000_000L, $"allocated {allocated / 1_000_000} MB");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void TotalNumbers_CappedAcrossAnnotations()
    {
        string nums = string.Join(",", Enumerable.Repeat("1", 900_000)) + ",";
        var sb = new StringBuilder();
        for (int i = 0; i < 6; i++)   // 6 x 1.8M = 10.8M > 8M 합계 상한(한 주석은 2M 안)
            sb.Append($"<polyline page=\"0\" rect=\"0,0,1,1\" name=\"p{i}\"><vertices>{nums}{nums}</vertices></polyline>");
        var r = Read(Wrap(sb.ToString()));
        Assert.True(r.Annotations.Count < 6);
        Assert.Contains(r.Skipped, s => s.Contains("too many coordinates"));
    }

    [Fact]
    public void ControlCharsStripped_AndLengthsCapped()
    {
        string longName = new('n', 500);
        var r = Read(Wrap($"<text page=\"0\" rect=\"0,0,1,1\" name=\"{longName}\" title=\"Ki&#127;m\" subject=\"{new string('s', 1000)}\"><contents>a&#133;b&#127;c\nline\td</contents></text>"));
        var a = Assert.Single(r.Annotations).A;
        Assert.Equal(128, a.Name!.Length);
        Assert.Equal("Kim", a.Author);
        Assert.Equal(256, a.Subject!.Length);
        Assert.Equal("abc\nline\td", a.Contents);
    }

    [Fact]
    public async Task ReplaceDifferentKind_Noted()
    {
        var s = await OpenBlankAsync();
        var sq = new Annotation { Id = s.Annotations.NewId(), Kind = AnnotKind.Square, Rect = new PdfRectF(0, 0, 10, 10), Name = "same" };
        await s.Commands.ExecuteAsync(PdfEditor.Document.Annotations.ChangeAnnotationsCommand.Add(s.Pages[0].Id, sq));
        var plan = XfdfImport.Plan(s, Read(Wrap("<circle page=\"0\" rect=\"0,0,10,10\" name=\"same\"/>")));
        Assert.Equal(1, plan.Imported);
        Assert.Contains(plan.Notes, n => n.Contains("different kind"));
    }
}
