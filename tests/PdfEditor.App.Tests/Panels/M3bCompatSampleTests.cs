using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Commands;
using PdfEditor.Exchange.Xfdf;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Panels;

/// <summary>
/// 다른 뷰어(Acrobat·PDF-XChange)로 점검할 V3 시료를 만든다: `spikes/out/m3b-export.xfdf`(우리 앱이 내보낸 XFDF)와
/// 그것을 가져올 주석 없는 `spikes/out/m3b-export.pdf` (docs/checklists/viewer-compat.md V3 절). 폴더가 git에 없으면 만들지 않고 넘어간다.
/// </summary>
public class M3bCompatSampleTests : PanelTestBase
{
    private static string OutDir([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "spikes", "out"));

    [Fact]
    public async Task CreateV3Sample_ExportsAndReimportsEverything()
    {
        string basePdf = Path.Combine(Dir, "base.pdf");
        TestPdfFactory.CreateBlank(basePdf, 2, 595.28f, 841.89f);
        var tab = await OpenTab(basePdf);
        var s = tab.Session!;
        await s.AnnotationsReady;

        var t0 = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.FromHours(9));
        Annotation M(AnnotKind k, string nm, PdfRectF r, Func<Annotation, Annotation>? f = null)
        {
            var a = new Annotation
            {
                Id = s.Annotations.NewId(), Kind = k, Rect = r, Color = new PdfColor(229, 55, 45), Name = nm, Author = "Kim", Subject = nm,
                Contents = nm, CreatedAt = t0, ModifiedAt = t0, Flags = PdfAnnotFlags.Print,
            };
            return f is null ? a : f(a);
        }
        var box = new PdfRectF(60, 700, 300, 740);
        var note = M(AnnotKind.Text, "sample-note", new PdfRectF(400, 760, 424, 784), a => a with { Color = new PdfColor(255, 212, 0), Contents = "메모: 이 치수 확인" });
        var reply = M(AnnotKind.Text, "sample-reply", note.Rect, a => a with { InReplyTo = note.Id, ReplyKind = ReplyType.Reply, Author = "Lee", Contents = "확인했습니다", Flags = ThreadAnnotations.ReplyFlags });
        var state = M(AnnotKind.Text, "sample-state", note.Rect, a => a with { InReplyTo = note.Id, ReplyKind = ReplyType.Reply, Author = "Lee", State = ReviewState.Accepted, Contents = "Lee set the status to Accepted", Flags = ThreadAnnotations.StateFlags });
        var pts = ImmutableArray.Create(new PdfPointF(80, 600), new PdfPointF(200, 640), new PdfPointF(320, 600));
        var page0 = new List<Annotation>
        {
            M(AnnotKind.Highlight, "sample-highlight", box, a => a with { Color = new PdfColor(255, 255, 0), Opacity = 0.5f, Quads = [PdfQuadF.FromRect(box)] }),
            note, reply, state,
            M(AnnotKind.Square, "sample-cloud", new PdfRectF(60, 400, 260, 520), a => a with { FillColor = new PdfColor(255, 245, 157), CloudIntensity = 1f, BorderWidth = 2 }),
            M(AnnotKind.Line, "sample-line", new PdfRectF(60, 300, 300, 360), a => a with { Vertices = [new PdfPointF(60, 300), new PdfPointF(300, 360)], EndEnding = LineEnding.OpenArrow, BorderWidth = 2 }),
            M(AnnotKind.Ink, "sample-ink", new PdfRectF(60, 200, 300, 260), a => a with { InkStrokes = [[new PdfPointF(60, 200), new PdfPointF(120, 260), new PdfPointF(200, 210), new PdfPointF(300, 250)]], BorderWidth = 2 }),
            M(AnnotKind.FreeText, "sample-text", new PdfRectF(320, 600, 560, 680), a => a with { Contents = "가나다 도면 검토 ABC", FontSizePt = 14, TextColor = new PdfColor(0, 0, 0), FillColor = new PdfColor(255, 245, 157) }),
        };
        var page1 = new List<Annotation>
        {
            M(AnnotKind.Polygon, "sample-polygon", new PdfRectF(80, 600, 320, 640), a => a with { Vertices = pts, FillColor = new PdfColor(200, 230, 255), BorderWidth = 2 }),
            M(AnnotKind.Stamp, "sample-stamp", new PdfRectF(100, 100, 260, 160), a => a with { Contents = null, Stamp = new StampSpec(StampKind.Builtin, "Approved", null, null) }),
        };
        await tab.RunAnnotationCommandAsync(new ChangeAnnotationsCommand(s.Pages[0].Id, [.. page0.Select(a => new AnnotationChange(null, a))]));
        await tab.RunAnnotationCommandAsync(new ChangeAnnotationsCommand(s.Pages[1].Id, [.. page1.Select(a => new AnnotationChange(null, a))]));

        string xfdf = Path.Combine(Dir, "m3b-export.xfdf");
        int n = await XfdfExporter.ExportAsync(s, AnnotationAssets.Empty, xfdf);
        Assert.Equal(page0.Count + page1.Count, n);

        // 같은 파일을 주석 없는 새 문서에 가져오면 모두 들어온다.
        var tab2 = await OpenTab(basePdf);
        await tab2.Session!.AnnotationsReady;
        var o = await XfdfImporter.ImportAsync(tab2, AnnotationAssets.Empty, xfdf);
        Assert.Equal(n, o.Imported);
        Assert.Equal(0, o.SkippedCount);
        Assert.Contains("가나다 도면 검토 ABC", File.ReadAllText(xfdf));

        // 점검용 파일을 spikes/out에 둔다(폴더가 있을 때만).
        string outDir = OutDir();
        if (Directory.Exists(outDir))
        {
            File.Copy(xfdf, Path.Combine(outDir, "m3b-export.xfdf"), overwrite: true);
            File.Copy(basePdf, Path.Combine(outDir, "m3b-export.pdf"), overwrite: true);
        }
        Assert.NotNull(XDocument.Load(xfdf).Root);
    }
}
