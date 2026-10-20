using System.Text;
using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Exchange.Xfdf;

namespace PdfEditor.Exchange.Tests;

/// <summary>XFDF 읽기 (M3b Task 6): 다른 프로그램이 만든 파일, 깨진 값, 보안 한계.</summary>
public class XfdfReaderTests
{
    private static long _next;
    private static AnnotId NewId() => new(Interlocked.Increment(ref _next));

    private static XfdfReadResult Read(string xml, AnnotationAssets? assets = null)
        => XfdfReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml)), NewId, assets ?? new AnnotationAssets());

    private static string Wrap(string annots) => $"<?xml version=\"1.0\"?><xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots>{annots}</annots></xfdf>";

    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", "xfdf", name);

    [Fact]
    public void FreeText_ColorIsBackground_BorderFromDaStroke_Rotation()
    {
        // 다른 편집기 관례(2026-10-20 버그): freetext의 color = 배경(/C), 테두리 색 = defaultappearance의 RG
        var r = Read(Wrap("""
            <freetext page="0" rect="0,0,100,40" color="#FFFF00" width="1" rotation="90" name="a">
              <defaultappearance>0 0 0 rg 0 0 1 RG /Helv 12 Tf</defaultappearance>
            </freetext>
            <freetext page="0" rect="0,0,100,40" color="#FF0000" interior-color="#00FF00" width="1" name="b"/>
            """));
        var a = r.Annotations[0].A;
        Assert.Equal(new PdfColor(255, 255, 0), a.FillColor);
        Assert.Equal(new PdfColor(0, 0, 255), a.Color);
        Assert.Equal(90, a.Rotation);
        // 이전 SedolPDF 파일(interior-color가 있음): color = 테두리
        var b = r.Annotations[1].A;
        Assert.Equal(new PdfColor(255, 0, 0), b.Color);
        Assert.Equal(new PdfColor(0, 255, 0), b.FillColor);
    }

    [Fact]
    public void Import_UnknownElementsSkipped_Reported()
    {
        var r = Read(Wrap("""
            <widget page="0" rect="0,0,1,1"/>
            <sound page="0" rect="0,0,1,1"/>
            <square page="0" rect="0,0,10,10" color="#FF0000" name="ok"/>
            """));
        Assert.Single(r.Annotations);
        Assert.Equal(2, r.Skipped.Count);
        Assert.Contains(r.Skipped, s => s.StartsWith("widget: ", StringComparison.Ordinal));
        Assert.Contains(r.Skipped, s => s.StartsWith("sound: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_PageProblems_Skipped()
    {
        var r = Read(Wrap("""
            <square page="-1" rect="0,0,10,10" name="a"/>
            <square page="abc" rect="0,0,10,10" name="b"/>
            <square rect="0,0,10,10" name="c"/>
            <square page="2" rect="0,0,10,10" name="d"/>
            """));
        Assert.Single(r.Annotations);
        Assert.Equal(2, r.Annotations[0].PageIndex);
        Assert.Equal(3, r.Skipped.Count);
        Assert.Contains(r.Skipped, s => s.Contains("negative"));
    }

    [Fact]
    public void Import_MalformedCoords_Skipped()
    {
        var r = Read(Wrap("""
            <highlight page="0" rect="0,0,10,10" coords="1,2,3,4,5,6,7" name="a"/>
            <highlight page="0" rect="0,0,10,10" coords="1,2,3,4,5,6,7,x" name="b"/>
            <square page="0" rect="0,0,10" name="c"/>
            <square page="0" rect="a,b,c,d" name="d"/>
            <polyline page="0" rect="0,0,10,10" name="e"><vertices>1,2,3</vertices></polyline>
            <ink page="0" rect="0,0,10,10" name="f"><inklist><gesture>1,2;3,q</gesture></inklist></ink>
            <line page="0" rect="0,0,10,10" start="1,2" end="3" name="g"/>
            <square page="0" rect="0,0,10,10" color="red" name="h"/>
            <square page="0" rect="0,0,10,10" opacity="lots" name="i"/>
            <highlight page="0" rect="0,0,10,10" coords="1,2,3,4,5,6,7,8" name="ok"/>
            """));
        Assert.Single(r.Annotations);
        Assert.Equal("ok", r.Annotations[0].A.Name);
        Assert.Equal(9, r.Skipped.Count);
        Assert.All(r.Skipped, s => Assert.Contains(": ", s));
    }

    [Fact]
    public void Read_NotXml_ThrowsFormatException()
    {
        var ex = Assert.Throws<XfdfFormatException>(() => Read("this is not xml"));
        Assert.Equal(XfdfFailure.NotValidXfdf, ex.Failure);
        Assert.Throws<XfdfFormatException>(() => Read(""));
        Assert.Throws<XfdfFormatException>(() => Read("<html><body/></html>"));
        using var fs = File.OpenRead(Fixture("malformed.xfdf"));
        Assert.Throws<XfdfFormatException>(() => XfdfReader.Read(fs, NewId, new AnnotationAssets()));
    }

    [Fact]
    public void Read_DtdWithExternalEntity_Rejected()
    {
        string secret = Path.Combine(Path.GetTempPath(), "xfdf-xxe-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(secret, "TOP-SECRET");
        try
        {
            string uri = new Uri(secret).AbsoluteUri;
            string xml = $"<?xml version=\"1.0\"?><!DOCTYPE xfdf [<!ENTITY xxe SYSTEM \"{uri}\">]><xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots><text page=\"0\" rect=\"0,0,1,1\"><contents>&xxe;</contents></text></annots></xfdf>";
            var ex = Assert.Throws<XfdfFormatException>(() => Read(xml));
            Assert.Equal(XfdfFailure.NotValidXfdf, ex.Failure);
        }
        finally { File.Delete(secret); }
    }

    [Fact]
    public void Read_EntityExpansionBomb_Rejected()
    {
        string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE xfdf [
              <!ENTITY a "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa">
              <!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">
              <!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">
              <!ENTITY d "&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;">
              <!ENTITY e "&d;&d;&d;&d;&d;&d;&d;&d;&d;&d;">
            ]>
            <xfdf xmlns="http://ns.adobe.com/xfdf/"><annots><text page="0" rect="0,0,1,1"><contents>&e;</contents></text></annots></xfdf>
            """;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<XfdfFormatException>(() => Read(xml));
        Assert.True(sw.ElapsedMilliseconds < 2000);
    }

    private sealed class FakeBigStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    /// <summary>길이를 알 수 없는(되감기 불가) 끝없는 스트림.</summary>
    private sealed class EndlessStream : Stream
    {
        private readonly byte[] _head = Encoding.UTF8.GetBytes("<xfdf><annots>");
        private readonly byte[] _chunk = Encoding.UTF8.GetBytes("<!-- " + new string('x', 4000) + " -->");
        private int _headPos;
        private long _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_headPos < _head.Length)
            {
                int n = Math.Min(count, _head.Length - _headPos);
                Array.Copy(_head, _headPos, buffer, offset, n);
                _headPos += n;
                return n;
            }
            int m = Math.Min(count, _chunk.Length);
            Array.Copy(_chunk, 0, buffer, offset, m);
            _pos += m;
            return m;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Read_TooLargeFile_Rejected()
    {
        var ex = Assert.Throws<XfdfFormatException>(() => XfdfReader.Read(new FakeBigStream(XfdfReader.MaxFileBytes + 1), NewId, new AnnotationAssets()));
        Assert.Equal(XfdfFailure.TooLarge, ex.Failure);
        var ex2 = Assert.Throws<XfdfFormatException>(() => XfdfReader.Read(new EndlessStream(), NewId, new AnnotationAssets()));
        Assert.Equal(XfdfFailure.TooLarge, ex2.Failure);
    }

    [Fact]
    public void Read_HugeAttachment_SkippedWithoutDecoding()
    {
        var big = new string('A', 40_000_000);   // base64 40M글자 = 약 30MB > 상한 25MB
        var assets = new AnnotationAssets();
        var r = Read(Wrap($"<fileattachment page=\"0\" rect=\"0,0,10,10\" file=\"big.bin\" name=\"big\"><data encoding=\"base64\">{big}</data></fileattachment>"), assets);
        Assert.Empty(r.Annotations);
        Assert.Single(r.Skipped);
        Assert.Contains("larger", r.Skipped[0]);
    }

    [Fact]
    public void Read_CompressedBomb_Skipped()
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.SmallestSize, true))
        {
            var zeros = new byte[1024 * 1024];
            for (int i = 0; i < 40; i++) z.Write(zeros);   // 40MB로 풀림
        }
        string b64 = Convert.ToBase64String(ms.ToArray());
        var r = Read(Wrap($"<fileattachment page=\"0\" rect=\"0,0,10,10\" file=\"z.bin\" name=\"z\"><data encoding=\"base64\" filter=\"FlateDecode\">{b64}</data></fileattachment>"));
        Assert.Empty(r.Annotations);
        Assert.Single(r.Skipped);
    }

    [Fact]
    public void Read_AttachmentNameSanitized_AndBytesStored()
    {
        var assets = new AnnotationAssets();
        var r = Read(Wrap("<fileattachment page=\"0\" rect=\"0,0,10,10\" file=\"..\\..\\evil.txt\" name=\"att\"><data encoding=\"base64\">SGVsbG8=</data></fileattachment>"), assets);
        var a = r.StoreAttachment(Assert.Single(r.Annotations).A, out var err)!;
        Assert.Null(err);
        Assert.Equal("evil.txt", a.AttachmentName);
        Assert.Equal(Encoding.ASCII.GetBytes("Hello"), assets.GetFile(a.AttachmentId!).Data);
    }

    [Fact]
    public void Read_AttachmentIntoReadOnlyAssets_SkippedNotThrown()
    {
        var r = Read(Wrap("<fileattachment page=\"0\" rect=\"0,0,10,10\" file=\"a.txt\" name=\"att\"><data encoding=\"base64\">SGVsbG8=</data></fileattachment>"), AnnotationAssets.Empty);
        Assert.Null(r.StoreAttachment(Assert.Single(r.Annotations).A, out var err));
        Assert.NotNull(err);
    }

    [Fact]
    public void Read_AttributeNames_CaseInsensitive()
    {
        var r = Read(Wrap("<Square Page=\"1\" RECT=\"10,20,30,40\" Color=\"#112233\" Interior-Color=\"#FFFFFF\" NAME=\"n1\" TITLE=\"Kim\" Width=\"3\" Opacity=\"0.25\" Flags=\"print,hidden\"><Contents>x</Contents></Square>"));
        var (page, a) = Assert.Single(r.Annotations);
        Assert.Equal(1, page);
        Assert.Equal(new PdfRectF(10, 20, 30, 40), a.Rect);
        Assert.Equal(new PdfColor(0x11, 0x22, 0x33), a.Color);
        Assert.Equal(new PdfColor(255, 255, 255), a.FillColor);
        Assert.Equal("n1", a.Name);
        Assert.Equal("Kim", a.Author);
        Assert.Equal(3f, a.BorderWidth);
        Assert.Equal(0.25f, a.Opacity);
        Assert.Equal(PdfAnnotFlags.Print | PdfAnnotFlags.Hidden, a.Flags);
        Assert.Equal("x", a.Contents);
    }

    [Fact]
    public void Read_ReplyAttributeCasing_BothWork()
    {
        var r = Read(Wrap("""
            <text page="0" rect="0,0,10,10" name="p"/>
            <text page="0" rect="0,0,10,10" name="r1" inreplyto="p" replyType="R"/>
            <text page="0" rect="0,0,10,10" name="r2" inReplyTo="p" replytype="r"/>
            <text page="0" rect="0,0,10,10" name="s1" inreplyto="p" state="accepted" StateModel="review"/>
            """));
        Assert.Equal(4, r.Annotations.Count);
        var p = r.Annotations[0].A;
        Assert.All(r.Annotations.Skip(1), x => Assert.Equal(p.Id, x.A.InReplyTo));
        Assert.Equal(ReviewState.Accepted, r.Annotations[3].A.State);
        Assert.Equal(ThreadAnnotations.StateFlags, r.Annotations[3].A.Flags);
    }

    [Fact]
    public void Read_StateProblems_Skipped()
    {
        var r = Read(Wrap("""
            <text page="0" rect="0,0,10,10" name="p"/>
            <text page="0" rect="0,0,10,10" name="s1" inreplyto="p" state="Marked" statemodel="Marked"/>
            <text page="0" rect="0,0,10,10" name="s2" inreplyto="p" state="Weird"/>
            <text page="0" rect="0,0,10,10" name="s3" state="Accepted"/>
            """));
        Assert.Single(r.Annotations);
        Assert.Equal(3, r.Skipped.Count);
    }

    [Fact]
    public void Read_UnknownLineEnding_NoneWithNote()
    {
        var r = Read(Wrap("<line page=\"0\" rect=\"0,0,10,10\" start=\"0,0\" end=\"10,10\" head=\"RClosedArrow\" tail=\"Circle\" name=\"l\"/>"));
        var a = Assert.Single(r.Annotations).A;
        Assert.Equal(LineEnding.None, a.StartEnding);
        Assert.Equal(LineEnding.Circle, a.EndEnding);
        Assert.Empty(r.Skipped);
        Assert.Contains(r.Notes, n => n.Contains("RClosedArrow"));
    }

    [Fact]
    public void Read_DuplicateNames_LaterOnesSkipped()
    {
        var r = Read(Wrap("""
            <square page="0" rect="0,0,10,10" name="same"/>
            <circle page="0" rect="0,0,10,10" name="same"/>
            """));
        Assert.Single(r.Annotations);
        Assert.Single(r.Skipped);
    }

    [Fact]
    public void Read_MissingName_GetsStableNewName_AndDates()
    {
        var r = Read(Wrap("<square page=\"0\" rect=\"0,0,10,10\" date=\"D:20260301103000+09'00'\" creationdate=\"garbage\"/>"));
        var a = Assert.Single(r.Annotations).A;
        Assert.False(string.IsNullOrEmpty(a.Name));
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 10, 30, 0, TimeSpan.FromHours(9)), a.ModifiedAt);
        Assert.Equal(a.ModifiedAt, a.CreatedAt);   // 잘못된 날짜는 무시하고 다른 쪽을 쓴다
    }

    [Fact]
    public void Import_AcrobatStyleFixture()
    {
        using var fs = File.OpenRead(Fixture("acrobat-style.xfdf"));
        var r = XfdfReader.Read(fs, NewId, new AnnotationAssets());
        var byName = r.Annotations.ToDictionary(x => x.A.Name!);

        // 들어온 것: highlight, text 3개(메모·답글·상태), square, ink, line, freetext, stamp = 9
        Assert.Equal(9, r.Annotations.Count);
        // widget, sound만 건너뜀. popup은 말풍선 위치 정보라 조용히 무시.
        Assert.Equal(2, r.Skipped.Count);

        var hl = byName["acro-hl-1"].A;
        Assert.Equal(AnnotKind.Highlight, hl.Kind);
        Assert.Equal("Check this value", hl.Contents);
        Assert.Equal("Kim Review", hl.Author);
        Assert.Single(hl.Quads);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 10, 30, 0, TimeSpan.FromHours(9)), hl.ModifiedAt);

        var note = byName["acro-note-1"].A;
        var reply = byName["acro-reply-1"].A;
        var state = byName["acro-state-1"].A;
        Assert.Equal(note.Id, reply.InReplyTo);
        Assert.Equal(note.Id, state.InReplyTo);
        Assert.Equal(ReviewState.Accepted, state.State);
        Assert.Null(reply.State);
        Assert.NotEqual(0, state.Flags & PdfAnnotFlags.Hidden);

        var sq = byName["acro-sq-1"].A;
        Assert.Equal(0.5f, sq.Opacity);
        Assert.Equal(new PdfColor(0xFF, 0xFF, 0xC0), sq.FillColor);

        var ink = byName["acro-ink-1"].A;
        Assert.Equal(2, ink.InkStrokes.Length);
        Assert.Equal(3, ink.InkStrokes[0].Length);

        var line = byName["acro-line-1"].A;
        Assert.Equal(LineEnding.OpenArrow, line.StartEnding);
        Assert.Equal(LineEnding.None, line.EndEnding);
        Assert.Equal(2, line.Vertices.Length);

        var ft = byName["acro-ft-1"].A;
        Assert.Equal("Revise drawing", ft.Contents);
        Assert.Equal(14f, ft.FontSizePt);
        Assert.Equal(TextAlign.Center, ft.Align);
        Assert.Equal(new PdfColor(26, 51, 76), ft.TextColor);

        var stamp = byName["acro-stamp-1"];
        Assert.Equal(1, stamp.PageIndex);
        Assert.Equal("Approved", stamp.A.Stamp!.BuiltinName);
    }

    [Fact]
    public void Import_PdfXChangeStyleFixture()
    {
        using var fs = File.OpenRead(Fixture("pdfxchange-style.xfdf"));
        var assets = new AnnotationAssets();
        var r = XfdfReader.Read(fs, NewId, assets);
        var byName = r.Annotations.ToDictionary(x => x.A.Name!);

        // squiggly는 지원하지 않는 요소 → 건너뜀
        Assert.Single(r.Skipped);
        Assert.StartsWith("squiggly: ", r.Skipped[0]);
        Assert.Equal(8, r.Annotations.Count);

        var hl = byName["pxc-hl-1"].A;   // 대소문자가 섞인 이름·공백 구분 좌표·공백이 낀 rect
        Assert.Equal(new PdfRectF(20, 160, 120, 180), hl.Rect);
        Assert.Single(hl.Quads);
        Assert.Equal(0.4f, hl.Opacity);
        Assert.Equal("Highlight from PDF-XChange", hl.Contents);
        Assert.Equal("Park", hl.Author);

        var pl = byName["pxc-pl-1"].A;   // 쉼표만 쓴 vertices
        Assert.Equal(3, pl.Vertices.Length);
        Assert.Equal(LineEnding.Diamond, pl.StartEnding);
        Assert.Equal(LineEnding.ClosedArrow, pl.EndEnding);

        var pg = byName["pxc-pg-1"];
        Assert.Equal(1, pg.PageIndex);
        Assert.Equal(3, pg.A.Vertices.Length);
        Assert.True(pg.A.CloudIntensity > 0);

        var reply = byName["pxc-reply-1"].A;
        Assert.Equal(byName["pxc-note-1"].A.Id, reply.InReplyTo);
        Assert.Equal(ThreadAnnotations.ReplyFlags, reply.Flags);

        var att = r.StoreAttachment(byName["pxc-att-1"].A, out _)!;
        Assert.Equal("notes.txt", att.AttachmentName);
        Assert.Equal(Encoding.ASCII.GetBytes("Hello"), assets.GetFile(att.AttachmentId!).Data);
    }

    [Fact]
    public void Read_UnresolvedParent_RecordedByName()
    {
        var r = Read(Wrap("<text page=\"0\" rect=\"0,0,10,10\" name=\"r\" inreplyto=\"elsewhere\"/>"));
        var a = Assert.Single(r.Annotations).A;
        Assert.Null(a.InReplyTo);
        Assert.Equal("elsewhere", r.ParentNames[a.Id]);
    }

    [Fact]
    public void Read_ReplyLinkOnNonTextKinds_IgnoredWithNote()
    {
        var r = Read(Wrap("""
            <text page="0" rect="0,0,10,10" name="p"/>
            <square page="0" rect="0,0,10,10" name="s" inreplyto="p"/>
            """));
        Assert.Equal(2, r.Annotations.Count);
        Assert.Null(r.Annotations[1].A.InReplyTo);
        Assert.Single(r.Notes);
    }

    [Fact]
    public void Read_EmptyAnnots_IsEmptyResult()
    {
        var r = Read("<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots/></xfdf>");
        Assert.Empty(r.Annotations);
        Assert.Empty(r.Skipped);
        var r2 = Read("<xfdf/>");
        Assert.Empty(r2.Annotations);
    }
}
