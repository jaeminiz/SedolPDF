using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Commands;
using PdfEditor.Exchange.Xfdf;
using PdfEditor.TestFixtures;

namespace PdfEditor.Exchange.Tests;

/// <summary>XFDF 내보내기 → 가져오기와 문서에 넣는 규칙 (M3b Task 6, Review Focus 3).</summary>
public class XfdfRoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-xfdf-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly List<DocumentSession> _sessions = [];
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.FromHours(9));
    private static readonly PdfRectF R = new(10f, 20f, 110.5f, 70f);

    public XfdfRoundTripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var s in _sessions) s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<DocumentSession> OpenBlankAsync(int pages = 2, string name = "blank.pdf")
    {
        string p = Path.Combine(_dir, name);
        TestPdfFactory.CreateBlank(p, pages, 300, 300);
        var s = await DocumentSession.OpenAsync(_t, p, null);
        _sessions.Add(s);
        await s.AnnotationsReady;
        return s;
    }

    private static Annotation Make(DocumentSession s, AnnotKind k, string nm, Func<Annotation, Annotation>? f = null)
    {
        var a = new Annotation
        {
            Id = s.Annotations.NewId(), Kind = k, Rect = R, Color = new PdfColor(255, 0, 0), Name = nm, Author = "Kim", Subject = "Sub",
            Contents = "c-" + nm, CreatedAt = T0, ModifiedAt = T0.AddMinutes(5), Flags = PdfAnnotFlags.Print,
        };
        return f is null ? a : f(a);
    }

    private static Task Add(DocumentSession s, int page, params Annotation[] items)
        => s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(s.Pages[page].Id, [.. items.Select(a => new AnnotationChange(null, a))]));

    private static List<(int Page, Annotation A)> All(DocumentSession s)
    {
        var l = new List<(int, Annotation)>();
        for (int i = 0; i < s.Pages.Length; i++)
            foreach (var v in s.Annotations.Get(s.Pages[i].Id).Items) l.Add((i, v.Data));
        return l;
    }

    private static XfdfReadResult RoundTrip(DocumentSession from, AnnotationAssets fromAssets, AnnotationAssets toAssets, out string xml)
    {
        var doc = XfdfWriter.Write([.. All(from).Select(x => (x.Page, x.A))], "x.pdf", fromAssets);
        xml = doc.ToString();
        using var ms = new MemoryStream();
        doc.Save(ms);
        ms.Position = 0;
        return XfdfReader.Read(ms, from.Annotations.NewId, toAssets);
    }

    private static string Pts(IEnumerable<PdfPointF> p) => string.Join(";", p.Select(x => $"{x.X.ToString(CultureInfo.InvariantCulture)},{x.Y.ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>비교용 문자열: Id 대신 NM으로 부모를 가리키고, 첨부는 이름과 바이트로.</summary>
    private static string Describe(int page, Annotation a, Func<AnnotId, string?> nm, AnnotationAssets assets)
    {
        var sb = new StringBuilder();
        void P(string k, object? v) => sb.Append(k).Append('=').Append(v is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : v).Append('|');
        P("page", page); P("kind", a.Kind); P("rect", a.Rect); P("color", a.Color); P("fill", a.FillColor);
        P("op", a.Opacity); P("w", a.BorderWidth); P("contents", a.Contents); P("author", a.Author); P("subject", a.Subject);
        P("mod", a.ModifiedAt?.ToUnixTimeSeconds()); P("cre", a.CreatedAt?.ToUnixTimeSeconds()); P("name", a.Name); P("flags", a.Flags);
        P("quads", string.Join(";", a.Quads.Select(q => Pts([q.P1, q.P2, q.P3, q.P4]))));
        P("ink", string.Join("/", a.InkStrokes.Select(s => Pts(s))));
        P("verts", Pts(a.Vertices)); P("le", $"{a.StartEnding}/{a.EndEnding}"); P("cloud", a.CloudIntensity);
        P("ls", a.LineStyle); P("l2", Pts(a.Leader2.IsDefault ? [] : a.Leader2));
        P("intent", a.Intent); P("tb", a.TextBox); P("fs", a.FontSizePt); P("tc", a.TextColor); P("align", a.Align);
        P("parent", a.InReplyTo is { } p ? nm(p) : null); P("state", a.State); P("rk", a.ReplyKind);
        P("stamp", a.Stamp is { } s ? $"{s.Kind}/{s.BuiltinName}/{s.Text}" : null);
        if (a.AttachmentId is { } id && assets.HasFile(id)) { var (n, d) = assets.GetFile(id); P("att", n + ":" + Convert.ToBase64String(d)); }
        return sb.ToString();
    }

    private static List<string> DescribeAll(IEnumerable<(int Page, Annotation A)> items, AnnotationAssets assets)
    {
        var list = items.ToList();
        var names = list.ToDictionary(x => x.A.Id, x => x.A.Name);
        return [.. list.Select(x => Describe(x.Page, x.A, id => names.GetValueOrDefault(id), assets)).OrderBy(s => s, StringComparer.Ordinal)];
    }

    [Fact]
    public async Task RoundTrip_ExportThenImport_Equivalent()
    {
        var src = await OpenBlankAsync();
        var assets = new AnnotationAssets();
        string fid = assets.AddFile("memo.txt", [1, 2, 3, 250, 251]);
        var pts = ImmutableArray.Create(new PdfPointF(1, 2), new PdfPointF(3.5f, 4), new PdfPointF(30, 40));
        var quad = ImmutableArray.Create(PdfQuadF.FromRect(R));

        var note = Make(src, AnnotKind.Text, "note");
        var reply = Make(src, AnnotKind.Text, "reply", a => a with { InReplyTo = note.Id, ReplyKind = ReplyType.Reply, Flags = ThreadAnnotations.ReplyFlags });
        var state = Make(src, AnnotKind.Text, "state", a => a with { InReplyTo = note.Id, ReplyKind = ReplyType.Reply, State = ReviewState.Accepted, Flags = ThreadAnnotations.StateFlags });
        var strike = Make(src, AnnotKind.StrikeOut, "strike", a => a with { Quads = quad });
        var caret = Make(src, AnnotKind.Caret, "caret", a => a with { InReplyTo = strike.Id, ReplyKind = ReplyType.Group });
        await Add(src, 0,
            Make(src, AnnotKind.Ink, "ink", a => a with { InkStrokes = [pts, pts.RemoveAt(2)], BorderWidth = 2.5f }),
            Make(src, AnnotKind.Highlight, "hl", a => a with { Quads = quad, Opacity = 0.5f, Color = new PdfColor(255, 255, 0) }),
            Make(src, AnnotKind.Underline, "ul", a => a with { Quads = quad }),
            strike, caret, note, reply, state,
            Make(src, AnnotKind.Square, "sq", a => a with { FillColor = new PdfColor(0, 255, 0), CloudIntensity = 1.5f, BorderWidth = 2 }),
            Make(src, AnnotKind.Circle, "ci", a => a with { FillColor = new PdfColor(0, 0, 255) }),
            Make(src, AnnotKind.Line, "ln", a => a with { Vertices = pts.RemoveAt(2), StartEnding = LineEnding.OpenArrow, EndEnding = LineEnding.Circle }),
            Make(src, AnnotKind.Line, "ln2", a => a with { Vertices = pts.RemoveAt(2), StartEnding = LineEnding.ClosedArrow, EndEnding = LineEnding.ClosedArrow, LineStyle = LineStyle.DashDot, BorderWidth = 2.5f }));
        await Add(src, 1,
            Make(src, AnnotKind.PolyLine, "pl", a => a with { Vertices = pts, LineStyle = LineStyle.Dotted, BorderWidth = 3, StartEnding = LineEnding.Diamond, EndEnding = LineEnding.ClosedArrow }),
            Make(src, AnnotKind.Polygon, "pg", a => a with { Vertices = pts, FillColor = new PdfColor(1, 2, 3), CloudIntensity = 1 }),
            Make(src, AnnotKind.FreeText, "ft", a => a with { TextColor = new PdfColor(0, 0, 255), FontSizePt = 14, Align = TextAlign.Center, Intent = FreeTextIntent.Callout, Vertices = pts.RemoveAt(2), TextBox = new PdfRectF(12, 22, 100, 60), EndEnding = LineEnding.OpenArrow, Leader2 = ImmutableArray.Create(new PdfPointF(200, 50), new PdfPointF(150, 45), new PdfPointF(100, 41)) }),
            Make(src, AnnotKind.FreeText, "ft2", a => a with { TextColor = new PdfColor(10, 20, 30), FontSizePt = 9.5f, Intent = FreeTextIntent.Typewriter }),
            Make(src, AnnotKind.Stamp, "st", a => a with { Contents = null, Stamp = new StampSpec(StampKind.Builtin, "Approved", null, null) }),
            Make(src, AnnotKind.Stamp, "st2", a => a with { Contents = null, Stamp = new StampSpec(StampKind.Text, "", "한글 스탬프", null) }),
            Make(src, AnnotKind.FileAttachment, "fa", a => a with { AttachmentId = fid, AttachmentName = "memo.txt" }));

        var toAssets = new AnnotationAssets();
        var read = RoundTrip(src, assets, toAssets, out string xml);
        Assert.Empty(read.Skipped);
        Assert.Empty(read.Notes);

        // 다른 문서(새 세션)에 가져온다 — 모든 유형·답글·상태·첨부가 같다.
        var dst = await OpenBlankAsync(2, "dst.pdf");
        var plan = XfdfImport.Plan(dst, read);
        Assert.Equal(All(src).Count, plan.Imported);
        await dst.Commands.ExecuteAsync(plan.Command!);

        Assert.Equal(DescribeAll(All(src), assets), DescribeAll(All(dst), toAssets));
        Assert.Contains("Accepted", xml);
    }

    private static string Xml(string annots) => $"<?xml version=\"1.0\"?><xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots>{annots}</annots></xfdf>";

    private static XfdfReadResult ReadXml(DocumentSession s, string annots, AnnotationAssets? assets = null)
        => XfdfReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(Xml(annots))), s.Annotations.NewId, assets ?? new AnnotationAssets());

    [Fact]
    public async Task Import_PageOutOfRange_Skipped()
    {
        var s = await OpenBlankAsync(2);
        var read = ReadXml(s, """
            <square page="0" rect="0,0,10,10" name="a"/>
            <square page="2" rect="0,0,10,10" name="b"/>
            <square page="99" rect="0,0,10,10" name="c"/>
            """);
        var cmd = XfdfImport.BuildCommand(s, read, out var skipped);
        Assert.Equal(2, skipped.Count);
        Assert.All(skipped, x => Assert.Contains("does not exist", x));
        await s.Commands.ExecuteAsync(cmd!);
        Assert.Single(All(s));
    }

    [Fact]
    public async Task Import_AllSkipped_NoCommand()
    {
        var s = await OpenBlankAsync(1);
        var read = ReadXml(s, "<square page=\"5\" rect=\"0,0,10,10\" name=\"a\"/>");
        Assert.Null(XfdfImport.BuildCommand(s, read, out var skipped));
        Assert.Single(skipped);
    }

    [Fact]
    public async Task Import_MalformedCoords_Skipped()
    {
        var s = await OpenBlankAsync(1);
        var read = ReadXml(s, """
            <highlight page="0" rect="0,0,10,10" coords="1,2,3" name="bad"/>
            <square page="0" rect="0,0,10,10" name="good"/>
            """);
        var cmd = XfdfImport.BuildCommand(s, read, out var skipped);
        Assert.Single(skipped);
        await s.Commands.ExecuteAsync(cmd!);
        Assert.Equal("good", Assert.Single(All(s)).A.Name);
    }

    [Fact]
    public async Task Import_ExistingNm_ReplacesThatAnnotation()
    {
        var s = await OpenBlankAsync(1);
        var old = Make(s, AnnotKind.Square, "keepme", a => a with { Contents = "old", Color = new PdfColor(1, 1, 1) });
        await Add(s, 0, old);

        var read = ReadXml(s, "<square page=\"0\" rect=\"5,5,50,50\" name=\"keepme\" color=\"#00FF00\"><contents>new</contents></square>");
        var plan = XfdfImport.Plan(s, read);
        Assert.Equal(1, plan.Imported);
        await s.Commands.ExecuteAsync(plan.Command!);

        var now = Assert.Single(All(s)).A;
        Assert.Equal(old.Id, now.Id);          // 같은 주석이 바뀌었다
        Assert.Equal("new", now.Contents);
        Assert.Equal(new PdfColor(0, 255, 0), now.Color);

        await s.Commands.UndoAsync();
        var back = Assert.Single(All(s)).A;
        Assert.Equal("old", back.Contents);
        Assert.Equal(old.Id, back.Id);
    }

    [Fact]
    public async Task Import_Twice_NoDuplicates()
    {
        var src = await OpenBlankAsync(1);
        var note = Make(src, AnnotKind.Text, "note");
        var reply = Make(src, AnnotKind.Text, "reply", a => a with { InReplyTo = note.Id, ReplyKind = ReplyType.Reply });
        await Add(src, 0, note, reply, Make(src, AnnotKind.Circle, "ci"));
        string xml = XfdfWriter.Write([.. All(src).Select(x => (x.Page, x.A))], "x.pdf", AnnotationAssets.Empty).ToString();

        var dst = await OpenBlankAsync(1, "dst.pdf");
        for (int i = 0; i < 2; i++)
        {
            var read = XfdfReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml)), dst.Annotations.NewId, new AnnotationAssets());
            var cmd = XfdfImport.BuildCommand(dst, read, out var skipped);
            Assert.Empty(skipped);
            await dst.Commands.ExecuteAsync(cmd!);
        }
        var all = All(dst);
        Assert.Equal(3, all.Count);
        var n = all.Single(x => x.A.Name == "note").A;
        Assert.Equal(n.Id, all.Single(x => x.A.Name == "reply").A.InReplyTo);   // 답글은 여전히 같은 부모 아래
    }

    [Fact]
    public async Task Import_UndoRemovesAll()
    {
        var s = await OpenBlankAsync(2);
        var read = ReadXml(s, """
            <square page="0" rect="0,0,10,10" name="a"/>
            <circle page="1" rect="0,0,10,10" name="b"/>
            <text page="1" rect="0,0,10,10" name="c"/>
            <text page="1" rect="0,0,10,10" name="d" inreplyto="c"/>
            """);
        var cmd = XfdfImport.BuildCommand(s, read, out _);
        await s.Commands.ExecuteAsync(cmd!);
        Assert.Equal(4, All(s).Count);
        await s.Commands.UndoAsync();   // 한 번으로 모두
        Assert.Empty(All(s));
        await s.Commands.RedoAsync();
        Assert.Equal(4, All(s).Count);
    }

    [Fact]
    public async Task Import_ReplyToDocumentAnnotation_LinksByNm()
    {
        var s = await OpenBlankAsync(1);
        var note = Make(s, AnnotKind.Text, "docnote");
        await Add(s, 0, note);
        var read = ReadXml(s, "<text page=\"0\" rect=\"0,0,10,10\" name=\"r\" inreplyto=\"docnote\"><contents>hi</contents></text><text page=\"0\" rect=\"0,0,10,10\" name=\"st\" inreplyto=\"docnote\" state=\"Rejected\"/>");
        await s.Commands.ExecuteAsync(XfdfImport.BuildCommand(s, read, out var skipped)!);
        Assert.Empty(skipped);
        Assert.Equal(3, s.Annotations.ThreadOf(s.Pages[0].Id, note.Id).Count);
        Assert.Equal(ReviewState.Rejected, s.Annotations.CurrentState(s.Pages[0].Id, note.Id));
    }

    [Fact]
    public async Task Import_ParentMissing_BecomesRoot_StateWithoutParentSkipped()
    {
        var s = await OpenBlankAsync(1);
        var read = ReadXml(s, """
            <text page="0" rect="0,0,10,10" name="orphan" inreplyto="nobody"><contents>x</contents></text>
            <text page="0" rect="0,0,10,10" name="orphanState" inreplyto="nobody" state="Accepted"/>
            """);
        var plan = XfdfImport.Plan(s, read);
        Assert.Equal(1, plan.Imported);
        Assert.Single(plan.Skipped);
        Assert.Contains(plan.Notes, n => n.Contains("normal annotation"));
        await s.Commands.ExecuteAsync(plan.Command!);
        var a = Assert.Single(All(s)).A;
        Assert.Null(a.InReplyTo);
        Assert.Null(a.ReplyKind);
    }

    [Fact]
    public async Task Import_ParentOnSkippedPage_ReplyBecomesRoot()
    {
        var s = await OpenBlankAsync(1);
        var read = ReadXml(s, """
            <text page="7" rect="0,0,10,10" name="p"/>
            <text page="0" rect="0,0,10,10" name="r" inreplyto="p"/>
            """);
        var plan = XfdfImport.Plan(s, read);
        Assert.Equal(1, plan.Imported);
        await s.Commands.ExecuteAsync(plan.Command!);
        Assert.Null(Assert.Single(All(s)).A.InReplyTo);
    }

    [Fact]
    public async Task Import_ReplyOnOtherPageThanParent_MovedNextToParent()
    {
        var s = await OpenBlankAsync(2);
        var read = ReadXml(s, """
            <text page="0" rect="0,0,10,10" name="p"/>
            <text page="1" rect="0,0,10,10" name="r" inreplyto="p"/>
            """);
        var plan = XfdfImport.Plan(s, read);
        await s.Commands.ExecuteAsync(plan.Command!);
        Assert.Equal(2, s.Annotations.Get(s.Pages[0].Id).Items.Length);
        Assert.Empty(s.Annotations.Get(s.Pages[1].Id).Items);
    }

    [Fact]
    public async Task Import_CircularReplies_Broken()
    {
        var s = await OpenBlankAsync(1);
        var read = ReadXml(s, """
            <text page="0" rect="0,0,10,10" name="a" inreplyto="b"/>
            <text page="0" rect="0,0,10,10" name="b" inreplyto="a"/>
            """);
        var plan = XfdfImport.Plan(s, read);
        Assert.Equal(2, plan.Imported);
        await s.Commands.ExecuteAsync(plan.Command!);
        Assert.All(All(s), x => Assert.Null(x.A.InReplyTo));
    }

    [Fact]
    public async Task Import_ReplaceParent_RepliesStayLinkedToExistingId()
    {
        var s = await OpenBlankAsync(1);
        var note = Make(s, AnnotKind.Text, "p");
        await Add(s, 0, note);
        var read = ReadXml(s, """
            <text page="0" rect="0,0,10,10" name="p"><contents>edited</contents></text>
            <text page="0" rect="0,0,10,10" name="r" inreplyto="p"/>
            """);
        await s.Commands.ExecuteAsync(XfdfImport.BuildCommand(s, read, out _)!);
        var all = All(s);
        Assert.Equal(2, all.Count);
        Assert.Equal(note.Id, all.Single(x => x.A.Name == "r").A.InReplyTo);
        Assert.Equal("edited", all.Single(x => x.A.Name == "p").A.Contents);
    }

    [Fact]
    public async Task Import_EncryptedDocument_OnlyM2KindsImported()
    {
        string p = Path.Combine(_dir, "enc.pdf");
        TestPdfFactory.CreateEncrypted(p, 1, 300, 300, "secret");
        var s = await DocumentSession.OpenAsync(_t, p, "secret");
        _sessions.Add(s);
        await s.AnnotationsReady;
        var read = ReadXml(s, """
            <ink page="0" rect="0,0,10,10" name="ink"><inklist><gesture>1,1;5,5</gesture></inklist></ink>
            <highlight page="0" rect="0,0,10,10" coords="0,10,10,10,0,0,10,0" name="hl"/>
            <square page="0" rect="0,0,10,10" name="sq"/>
            <square page="0" rect="0,0,10,10" name="cloud" style="cloudy" intensity="1"/>
            <circle page="0" rect="0,0,10,10" name="ci"/>
            <text page="0" rect="0,0,10,10" name="note"/>
            <text page="0" rect="0,0,10,10" name="reply" inreplyto="note"/>
            <line page="0" rect="0,0,10,10" start="0,0" end="9,9" name="line"/>
            <freetext page="0" rect="0,0,10,10" name="ft"><contents>x</contents></freetext>
            <stamp page="0" rect="0,0,10,10" name="stamp" icon="Approved"/>
            """);
        var plan = XfdfImport.Plan(s, read);
        Assert.Equal(5, plan.Imported);
        Assert.Equal(5, plan.Skipped.Count);
        Assert.All(plan.Skipped, x => Assert.Contains("encrypted", x));
        await s.Commands.ExecuteAsync(plan.Command!);
        Assert.Equal(["ci", "hl", "ink", "note", "sq"], All(s).Select(x => x.A.Name!).Order().ToArray());
    }
}
