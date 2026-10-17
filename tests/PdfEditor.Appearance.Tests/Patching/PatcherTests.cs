using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Appearance.Tests.Patching;

public class PatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-patch-" + Guid.NewGuid().ToString("N"));
    private readonly PdfSharpAnnotationPatcher _patcher = new(() => []);

    public PatcherTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Mixed()
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        AnnotatedPdfFactory.CreateMixed(p);
        return p;
    }

    private static PdfArray AnnotArray(PdfSharp.Pdf.PdfDocument doc)
    {
        var item = doc.Pages[0].Elements["/Annots"];
        return (PdfArray)((item as PdfReference)?.Value ?? item)!;
    }

    private static PdfDictionary Annot(PdfSharp.Pdf.PdfDocument doc, int i)
    {
        var item = AnnotArray(doc).Elements[i];
        return (PdfDictionary)((item as PdfReference)?.Value ?? item);
    }

    private static int AnnotCount(PdfSharp.Pdf.PdfDocument doc) => AnnotArray(doc).Elements.Count;

    private static string Subtype(PdfSharp.Pdf.PdfDocument doc, int i) => ((PdfName)Annot(doc, i).Elements["/Subtype"]!).Value;

    private static PdfSharp.Pdf.PdfDocument Read(string path) => PdfReader.Open(path, PdfDocumentOpenMode.Import);

    private static ImmutableArray<PatchOp> Ops(params PatchOp[] ops) => [.. ops];

    private static PatchPlan Plan(AnnotationPatch[]? patches = null, AnnotationCreate[]? creates = null, AnnotationRemoval[]? removals = null)
        => new([.. patches ?? []], [.. creates ?? []], [.. removals ?? []]);

    private static AnnotationCreate NewLine(string nm = "line-1") => new(0, "Line", nm, new PdfRectF(50, 50, 150, 90), 4,
        Ops(new SetNumbers("/L", [50f, 50f, 150f, 90f]),
            new SetAppearanceStream("1 0 0 RG 2 w 50 50 m 150 90 l S", new PdfRectF(50, 50, 150, 90))));

    // CreateMixed의 주석에는 /NM이 없으므로 Nm은 ""이다.

    [Fact]
    public void Patch_SetNamesAndNumbers_WrittenAndUnknownKeysKept()
    {
        var p = Mixed();
        var plan = Plan(patches: [new AnnotationPatch(0, 1, "", Ops(
            new SetNumbers("/RD", [1f, 1f, 1f, 1f]),
            new SetNames("/LE", ["OpenArrow", "None"]),
            new SetName("/IT", "LineArrow"),
            new SetString("/Subj", "S"),
            new SetInteger("/XInt", 7),
            new SetBorderEffect(1)))]);
        _patcher.Apply(p, null, plan, AnnotationAssets.Empty);

        using var doc = Read(p);
        var sq = Annot(doc, 1);
        Assert.Equal(4, ((PdfArray)sq.Elements["/RD"]!).Elements.Count);
        Assert.Equal("/OpenArrow", ((PdfName)((PdfArray)sq.Elements["/LE"]!).Elements[0]).Value);
        Assert.Equal("/LineArrow", ((PdfName)sq.Elements["/IT"]!).Value);
        Assert.Equal("S", sq.Elements.GetString("/Subj"));
        Assert.Equal(7, sq.Elements.GetInteger("/XInt"));
        Assert.Equal("/C", ((PdfName)((PdfDictionary)sq.Elements["/BE"]!).Elements["/S"]!).Value);
        Assert.Equal("abc", sq.Elements.GetString("/XKeep"));
        Assert.Equal(13, AnnotCount(doc));
    }

    [Fact]
    public void Patch_RemoveKeys_RemovesOnlyListed()
    {
        var p = Mixed();
        _patcher.Apply(p, null, Plan(patches: [new AnnotationPatch(0, 1, "", Ops(new RemoveKeys(["/BS", "/Missing"])))]), AnnotationAssets.Empty);
        using var doc = Read(p);
        Assert.False(Annot(doc, 1).Elements.ContainsKey("/BS"));
        Assert.Equal("abc", Annot(doc, 1).Elements.GetString("/XKeep"));
    }

    [Fact]
    public void Patch_NmMismatch_Throws()
    {
        var p = Mixed();
        var plan = Plan(patches: [new AnnotationPatch(0, 1, "someone-else", Ops(new SetInteger("/X", 1)))]);
        Assert.Throws<InvalidOperationException>(() => _patcher.Apply(p, null, plan, AnnotationAssets.Empty));
    }

    [Fact]
    public async Task Create_AppendsDictWithAppearance()
    {
        var p = Mixed();
        _patcher.Apply(p, null, Plan(creates: [NewLine()]), AnnotationAssets.Empty);

        using var t = new PdfiumThread();
        await t.InvokeAsync(() =>
        {
            using var doc = Pdf.Native.PdfDocument.Open(t, p);
            using var page = doc.LoadPage(0);
            Assert.Equal(14, page.AnnotationCount);
            using var a = page.GetAnnotation(13);
            Assert.Equal(4, a.Subtype);
            Assert.False(string.IsNullOrEmpty(a.GetAppearance()));
            Assert.Equal(4, a.Flags & 4);
        });
        using var d = Read(p);
        Assert.Equal("line-1", Annot(d, 13).Elements.GetString("/NM"));
    }

    [Fact]
    public void EnsurePopup_AddsOnce_LinkedBothWays()
    {
        var p = Mixed();
        var plan = Plan(patches: [new AnnotationPatch(0, 1, "", Ops(new EnsurePopup(new PdfRectF(300, 600, 480, 720), false)))]);
        _patcher.Apply(p, null, plan, AnnotationAssets.Empty);
        // 같은 계획을 한 번 더 적용해도 Popup은 그대로 1개(이미 있으면 아무것도 안 함)
        _patcher.Apply(p, null, plan, AnnotationAssets.Empty);

        using var doc = Read(p);
        Assert.Equal(14, AnnotCount(doc));
        var sq = Annot(doc, 1);
        var popup = (PdfDictionary)((PdfReference)sq.Elements["/Popup"]!).Value;
        Assert.Equal("/Popup", ((PdfName)popup.Elements["/Subtype"]!).Value);
        Assert.Same(Annot(doc, 13), popup);
        Assert.Equal(sq.Reference!.ObjectID, ((PdfReference)popup.Elements["/Parent"]!).ObjectID);
        Assert.False(popup.Elements.GetBoolean("/Open"));
    }

    [Fact]
    public void EnsurePopup_ExistingPopup_NoChange()
    {
        var p = Mixed();
        var plan = Plan(patches: [new AnnotationPatch(0, 3, "", Ops(new EnsurePopup(new PdfRectF(0, 0, 10, 10), true)))]);
        _patcher.Apply(p, null, plan, AnnotationAssets.Empty);

        using var doc = Read(p);
        Assert.Equal(13, AnnotCount(doc));
        var popup = (PdfDictionary)((PdfReference)Annot(doc, 3).Elements["/Popup"]!).Value;
        Assert.Same(Annot(doc, 4), popup);                                 // 원래 Popup 그대로
        Assert.False(popup.Elements.GetBoolean("/Open"));                  // 우리 값(true)으로 바뀌지 않음
    }

    [Fact]
    public void Removals_DescendingIndices_PopupAndRepliesGo()
    {
        var p = Mixed();
        // Text [3], Popup [4], 답글 [5] — 일부러 섞인 순서로 준다
        _patcher.Apply(p, null, Plan(removals: [new AnnotationRemoval(0, [3, 5, 4])]), AnnotationAssets.Empty);

        using var doc = Read(p);
        Assert.Equal(10, AnnotCount(doc));
        for (int i = 0; i < 10; i++)
        {
            Assert.NotEqual("/Popup", Subtype(doc, i));
            Assert.False(Annot(doc, i).Elements.ContainsKey("/IRT"));
        }
        Assert.Equal("/Square", Subtype(doc, 3));   // 원래 [6] 클라우드
    }

    [Fact]
    public void Order_PatchesThenCreatesThenRemovals()
    {
        var p = Mixed();
        var plan = Plan(
            patches: [new AnnotationPatch(0, 5, "", Ops(new SetInteger("/XMark", 1)))],   // 제거 전 번호 [5] = 답글
            creates: [NewLine()],
            removals: [new AnnotationRemoval(0, [0])]);                                    // [0] Link
        _patcher.Apply(p, null, plan, AnnotationAssets.Empty);

        using var doc = Read(p);
        Assert.Equal(13, AnnotCount(doc));   // 13 + 1 - 1
        Assert.NotEqual("/Link", Subtype(doc, 0));
        // 표시가 붙은 것은 답글(번호가 하나 당겨져 [4])
        Assert.Equal(1, Annot(doc, 4).Elements.GetInteger("/XMark"));
        Assert.Equal(AnnotatedPdfFactory.ReplyText, Annot(doc, 4).Elements.GetString("/Contents"));
        Assert.Equal("/Line", Subtype(doc, 12));
    }

    [Fact]
    public void InReplyTo_LinksToNewlyCreatedParent()
    {
        var p = Mixed();
        var child = new AnnotationCreate(0, "Text", "child-1", new PdfRectF(10, 10, 30, 30), 4,
            Ops(new SetInReplyTo("parent-1", "Group")));
        _patcher.Apply(p, null, Plan(creates: [child, NewLine("parent-1")]), AnnotationAssets.Empty);   // 자식이 먼저 와도 부모를 찾는다

        using var doc = Read(p);
        var c = Annot(doc, 13);
        Assert.Equal("child-1", c.Elements.GetString("/NM"));
        Assert.Equal("parent-1", ((PdfDictionary)((PdfReference)c.Elements["/IRT"]!).Value).Elements.GetString("/NM"));
        Assert.Equal("/Group", ((PdfName)c.Elements["/RT"]!).Value);
    }
}
