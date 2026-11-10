#pragma warning disable xUnit1031 // PDFium 전용 스레드 호출을 동기로 기다린다(시험 도우미)
using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Patching;
using PdfEditor.Document.Content;
using PdfEditor.Document.Output;
using PdfEditor.TestFixtures;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using NativeDoc = Pdf.Native.PdfDocument;

namespace PdfEditor.Appearance.Tests.Patching;

/// <summary>0.9.0 Task 4: 원본 개체 복제를 저장 파일에 쓰기(같은 쪽 / 다른 쪽 + 자원 옮기기).</summary>
public sealed class DuplicatePatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-duppatch-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly PdfSharpAnnotationPatcher _patcher = new(() => []);

    static DuplicatePatchTests() => System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TestPdfFactory).TypeHandle);   // 글꼴 해석기 등록

    public DuplicatePatchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        _patcher.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string NewPath() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");

    private ImmutableArray<ReferenceObj> Refs(string path, int pageIndex) => _t.InvokeAsync(() =>
    {
        using var doc = NativeDoc.Open(_t, path, null);
        using var page = doc.LoadPage(pageIndex);
        var b = ImmutableArray.CreateBuilder<ReferenceObj>();
        for (int i = 0; i < page.ObjectCount; i++)
        {
            var o = page.GetObject(i);
            var r = o.Bounds;
            b.Add(new ReferenceObj((int)o.Type, r.Left, r.Bottom, r.Right, r.Top));
        }
        return b.ToImmutable();
    }).Result;

    /// <summary>1쪽: 글 "SOURCE" + 사각형. 2쪽: 사각형만(글꼴 자원 없음).</summary>
    private string TwoPages()
    {
        string p = NewPath();
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var p1 = doc.AddPage(); p1.Width = XUnit.FromPoint(595); p1.Height = XUnit.FromPoint(842);
        using (var g = XGraphics.FromPdfPage(p1))
        {
            g.DrawString("SOURCE", new XFont("Arial", 20, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode)), XBrushes.Black, new XPoint(100, 100));
            g.DrawRectangle(new XPen(XColors.Red, 2), 200, 200, 100, 50);
        }
        var p2 = doc.AddPage(); p2.Width = XUnit.FromPoint(595); p2.Height = XUnit.FromPoint(842);
        using (var g = XGraphics.FromPdfPage(p2)) g.DrawRectangle(new XPen(XColors.Blue, 1), 50, 50, 80, 40);
        doc.Save(p);
        return p;
    }

    private void Run(string path, params PageContentRewrite[] rewrites) =>
        _patcher.Apply(path, null, new PatchPlan([], [], []) { ContentRewrites = [.. rewrites] }, new AnnotationAssets());

    private (int Index, PdfRectF Bounds) First(string path, int pageIndex, PdfPageObjectType type) => _t.InvokeAsync(() =>
    {
        using var doc = NativeDoc.Open(_t, path, null);
        using var page = doc.LoadPage(pageIndex);
        for (int i = 0; i < page.ObjectCount; i++)
            if (page.GetObject(i).Type == type) return (i, page.GetObject(i).Bounds);
        throw new InvalidOperationException("no object of type " + type);
    }).Result;

    [Fact]
    public void Patch_SamePageDuplicate_ObjectCountPlusOne()
    {
        string p = TwoPages();
        var refs = Refs(p, 0);
        var (idx, b) = First(p, 0, PdfPageObjectType.Path);
        var pid = new PageId(1);
        Run(p, new PageContentRewrite(0, [new DuplicateObjectEdit(1, pid, idx, PdfMatrix.Translate(30, -30))], refs, [new SourcePageRef(pid, 0, refs)]));

        var (count, last) = _t.InvokeAsync(() =>
        {
            using var doc = NativeDoc.Open(_t, p, null);
            using var page = doc.LoadPage(0);
            return (page.ObjectCount, page.GetObject(page.ObjectCount - 1).Bounds);
        }).Result;
        Assert.Equal(refs.Length + 1, count);
        Assert.InRange(last.Left, b.Left + 30 - 1, b.Left + 30 + 1);
        Assert.InRange(last.Bottom, b.Bottom - 30 - 1, b.Bottom - 30 + 1);
    }

    [Fact]
    public void Patch_CrossPageDuplicate_CopiesFontResource()
    {
        string p = TwoPages();
        var refs1 = Refs(p, 0); var refs2 = Refs(p, 1);
        var (idx, _) = First(p, 0, PdfPageObjectType.Text);
        var src = new PageId(1);
        Run(p, new PageContentRewrite(1, [new DuplicateObjectEdit(1, src, idx, PdfMatrix.Identity)], refs2, [new SourcePageRef(src, 0, refs1)]));

        using (var doc = PdfReader.Open(p, PdfDocumentOpenMode.Import))
        {
            var f1 = doc.Pages[0].Resources.Elements.GetDictionary("/Font");
            var f2 = doc.Pages[1].Resources.Elements.GetDictionary("/Font");
            Assert.NotNull(f2);
            Assert.Equal(f1!.Elements.Keys.Count, f2!.Elements.Keys.Count);
        }
        string text = _t.InvokeAsync(() =>
        {
            using var doc = NativeDoc.Open(_t, p, null);
            using var page = doc.LoadPage(1);
            using var tp = page.LoadText();
            return tp.GetText(0, tp.CharCount);
        }).Result;
        Assert.Contains("SOURCE", text);
    }

    [Fact]
    public void Patch_CrossPageDuplicate_MissingResource_Throws()
    {
        string p = TwoPages();
        var refs1 = Refs(p, 0); var refs2 = Refs(p, 1);
        var (idx, _) = First(p, 0, PdfPageObjectType.Text);
        // 원천 쪽의 글꼴 자원을 일부러 지운다
        using (var doc = PdfReader.Open(p, PdfDocumentOpenMode.Modify))
        {
            doc.Pages[0].Resources.Elements.Remove("/Font");
            doc.Save(p);
        }
        var src = new PageId(1);
        var ex = Assert.Throws<ContentRegenerationException>(() =>
            Run(p, new PageContentRewrite(1, [new DuplicateObjectEdit(1, src, idx, PdfMatrix.Identity)], refs2, [new SourcePageRef(src, 0, refs1)])));
        Assert.Contains("not found on source page", ex.Reason);
    }
}
