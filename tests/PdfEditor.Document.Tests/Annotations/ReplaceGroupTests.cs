using Pdf.Native;
using PdfEditor.Appearance;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Document.Tests.Annotations;

/// <summary>텍스트 바꾸기 묶음: StrikeOut + Caret (/IRT, /RT /Group) 저장·읽기·삭제 (계획 Task 11).</summary>
public class ReplaceGroupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-rg-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly PdfAppearanceSource _ap = new();
    private readonly PdfSharpAnnotationPatcher _patcher = new(() => []);

    private static readonly PdfColor Red = new(229, 55, 45);
    private static readonly PdfColor Blue = new(30, 90, 220);

    public ReplaceGroupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        _patcher.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);
    private SaveService Svc() => new([], _ap, _patcher);

    private async Task<DocumentSession> OpenBlankAsync()
    {
        TestPdfFactory.CreateBlank(P("blank.pdf"), 1, 200, 200);
        var s = await DocumentSession.OpenAsync(_t, P("blank.pdf"), null);
        await s.AnnotationsReady;
        return s;
    }

    /// <summary>StrikeOut(Name 없음: PDFium 길) + Caret 묶음.</summary>
    private static (Annotation Strike, Annotation Caret) NewGroup(DocumentSession s)
    {
        var strike = new Annotation
        {
            Id = s.Annotations.NewId(), Kind = AnnotKind.StrikeOut, Rect = new PdfRectF(40, 100, 140, 115), Color = Red,
            Quads = [new PdfQuadF(new(40, 115), new(140, 115), new(40, 100), new(140, 100))],
            Flags = PdfAnnotFlags.Print, Author = "tester",
        };
        var caret = new Annotation
        {
            Id = s.Annotations.NewId(), Kind = AnnotKind.Caret, Rect = new PdfRectF(136, 96, 152, 116), Color = Blue,
            Contents = "new text", Flags = PdfAnnotFlags.Print, Author = "tester",
            InReplyTo = strike.Id, ReplyKind = ReplyType.Group,
        };
        return (strike, caret);
    }

    private static Task AddGroup(DocumentSession s, PageId page, Annotation strike, Annotation caret)
        => s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(page, [new AnnotationChange(null, strike), new AnnotationChange(null, caret)]));

    private static IReadOnlyList<OriginalAnnotation> ReadBack(string pdf)
        => AnnotationReader.Read(File.ReadAllBytes(pdf), null, [0])[0];

    private static T Deref<T>(PdfItem? item) where T : PdfItem => (T)((item as PdfReference)?.Value ?? item)!;

    private static List<PdfDictionary> Annots(PdfSharp.Pdf.PdfDocument doc)
        => doc.Pages[0].Elements["/Annots"] is { } a ? [.. Deref<PdfArray>(a).Elements.Select(Deref<PdfDictionary>)] : [];

    [Fact]
    public async Task ReplaceGroup_SaveReopen_IrtAndRtGroup()
    {
        await using var s = await OpenBlankAsync();
        var id = s.Pages[0].Id;
        var (strike, caret) = NewGroup(s);
        await AddGroup(s, id, strike, caret);
        string target = P("group.pdf");
        await Svc().SaveAsync(s, target);

        using (var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import))
        {
            var list = Annots(doc);
            var sd = Assert.Single(list, d => d.Elements.GetName("/Subtype") == "/StrikeOut");
            var cd = Assert.Single(list, d => d.Elements.GetName("/Subtype") == "/Caret");
            Assert.Same(sd, Deref<PdfDictionary>(cd.Elements["/IRT"]));
            Assert.Equal("/Group", cd.Elements.GetName("/RT"));
            Assert.False(string.IsNullOrEmpty(sd.Elements.GetString("/NM")));
        }

        var back = ReadBack(target);
        Assert.All(back, o => Assert.Equal(ReadOnlyReason.None, o.Reason));
        var rs = back.Single(o => o.Data!.Kind == AnnotKind.StrikeOut);
        var rc = back.Single(o => o.Data!.Kind == AnnotKind.Caret);
        Assert.Equal(rs.Index, rc.GroupParentIndex);
        Assert.Equal("new text", rc.Data!.Contents);
    }

    [Fact]
    public async Task ReplaceGroup_RepeatedSaves_NoDuplicateCaret_SameNm()
    {
        await using var s = await OpenBlankAsync();
        var id = s.Pages[0].Id;
        var (strike, caret) = NewGroup(s);
        await AddGroup(s, id, strike, caret);

        var nms = new List<(string Strike, string Caret)>();
        for (int i = 0; i < 3; i++)
        {
            string target = P($"rep{i}.pdf");
            await Svc().SaveAsync(s, target);
            using var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import);
            var list = Annots(doc);
            Assert.Equal(2, list.Count);
            var sd = Assert.Single(list, d => d.Elements.GetName("/Subtype") == "/StrikeOut");
            var cd = Assert.Single(list, d => d.Elements.GetName("/Subtype") == "/Caret");
            Assert.Same(sd, Deref<PdfDictionary>(cd.Elements["/IRT"]));
            nms.Add((sd.Elements.GetString("/NM"), cd.Elements.GetString("/NM")));
        }
        Assert.Single(nms.Distinct());
    }

    [Fact]
    public async Task DeleteGroup_RemovesBoth_UndoRestoresBoth()
    {
        await using var s = await OpenBlankAsync();
        var id = s.Pages[0].Id;
        var (strike, caret) = NewGroup(s);
        await AddGroup(s, id, strike, caret);

        var group = s.Annotations.GroupOf(id, caret.Id);
        Assert.Equal([strike.Id, caret.Id], group.Select(a => a.Id));
        Assert.Equal([strike.Id, caret.Id], s.Annotations.GroupOf(id, strike.Id).Select(a => a.Id));

        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Delete(id, group));
        Assert.Empty(s.Annotations.Get(id).Items);
        await Svc().SaveAsync(s, P("deleted.pdf"));
        Assert.Empty(ReadBack(P("deleted.pdf")));

        await s.Commands.UndoAsync();
        Assert.Equal(2, s.Annotations.Get(id).Items.Length);
        Assert.Equal([strike.Id, caret.Id], s.Annotations.GroupOf(id, caret.Id).Select(a => a.Id));
        await Svc().SaveAsync(s, P("restored.pdf"));
        Assert.Equal(2, ReadBack(P("restored.pdf")).Count);
    }

    [Fact]
    public async Task OriginalReplaceGroupFromAcrobatLikeFixture_ReadAsGroup()
    {
        AnnotatedPdfFactory.CreateReplaceGroup(P("fx.pdf"));
        var back = ReadBack(P("fx.pdf"));
        Assert.Equal(ReadOnlyReason.None, back[0].Reason);
        Assert.Equal(ReadOnlyReason.None, back[1].Reason);
        Assert.Equal(0, back[1].GroupParentIndex);
        // 부모가 StrikeOut이 아닌 Caret은 읽기 전용 답글
        Assert.Equal(ReadOnlyReason.Reply, back[3].Reason);

        await using var s = await DocumentSession.OpenAsync(_t, P("fx.pdf"), null);
        await s.AnnotationsReady;
        var id = s.Pages[0].Id;
        var items = s.Annotations.Get(id).Items;
        Assert.Equal(3, items.Length);   // StrikeOut, Caret, Text 메모 (답글 Caret은 편집 불가)
        var strike = items.Single(v => v.Data.Kind == AnnotKind.StrikeOut).Data;
        var caret = items.Single(v => v.Data.Kind == AnnotKind.Caret).Data;
        Assert.Equal(strike.Id, caret.InReplyTo);
        Assert.Equal(ReplyType.Group, caret.ReplyKind);
        Assert.Equal([strike.Id, caret.Id], s.Annotations.GroupOf(id, strike.Id).Select(a => a.Id));

        // 묶음 삭제 → 저장: 둘 다 사라지고 편집 불가 답글 Caret과 메모는 남는다.
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Delete(id, s.Annotations.GroupOf(id, caret.Id)));
        await Svc().SaveAsync(s, P("fx-out.pdf"));
        using var doc = PdfReader.Open(P("fx-out.pdf"), PdfDocumentOpenMode.Import);
        var subs = Annots(doc).Select(d => d.Elements.GetName("/Subtype")).ToList();
        Assert.Equal(["/Text", "/Caret"], subs);
    }

    [Fact]
    public async Task OriginalReplaceGroup_TouchedCaret_KeepsIrt_NoDuplicates()
    {
        AnnotatedPdfFactory.CreateReplaceGroup(P("fx2.pdf"));
        await using var s = await DocumentSession.OpenAsync(_t, P("fx2.pdf"), null);
        await s.AnnotationsReady;
        var id = s.Pages[0].Id;
        var caret = s.Annotations.Get(id).Items.Single(v => v.Data.Kind == AnnotKind.Caret).Data;
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(id, [(caret, caret with { Color = Red })]));
        for (int i = 0; i < 2; i++)
        {
            await Svc().SaveAsync(s, P($"fx2-out{i}.pdf"));
            using var doc = PdfReader.Open(P($"fx2-out{i}.pdf"), PdfDocumentOpenMode.Import);
            var list = Annots(doc);
            Assert.Equal(4, list.Count);
            var cd = list.Single(d => d.Elements.GetString("/NM") == "caret-1");
            Assert.Equal("strike-1", Deref<PdfDictionary>(cd.Elements["/IRT"]).Elements.GetString("/NM"));
            Assert.Equal("/Group", cd.Elements.GetName("/RT"));
        }
    }

    [Fact]
    public async Task Encrypted_FailedCaretMember_StillHidesStrikeOutParent()
    {
        AnnotatedPdfFactory.CreateReplaceGroup(P("g.pdf"));
        // Caret에 /State를 넣어 읽기에 실패하게 만든 뒤 열기 암호를 건다 (패치를 못 하는 문서)
        using (var doc = PdfReader.Open(P("g.pdf"), PdfDocumentOpenMode.Modify))
        {
            var annots = Deref<PdfArray>(doc.Pages[0].Elements["/Annots"]);
            var caret = Deref<PdfDictionary>(annots.Elements[1]);
            caret.Elements["/State"] = new PdfString("Accepted");
            doc.SecuritySettings.UserPassword = "pw";
            doc.SecuritySettings.OwnerPassword = "pw-owner";
            doc.Save(P("g-enc.pdf"));
        }

        var raw = AnnotationReader.Read(File.ReadAllBytes(P("g-enc.pdf")), "pw", [0])[0];
        Assert.Null(raw[1].Data);
        Assert.Equal(0, raw[1].GroupParentIndex);   // 실패해도 부모 번호는 남는다

        await using var s = await DocumentSession.OpenAsync(_t, P("g-enc.pdf"), "pw");
        await s.AnnotationsReady;
        var kinds = s.Annotations.Get(s.Pages[0].Id).Items.Select(v => v.Data.Kind).ToList();
        Assert.DoesNotContain(AnnotKind.StrikeOut, kinds);   // 부모 StrikeOut은 모델에 없다 (PDFium이 원본 모양으로 그린다)
        Assert.DoesNotContain(AnnotKind.Caret, kinds);
    }
}
