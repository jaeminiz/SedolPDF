using System.Runtime.CompilerServices;
using Pdf.Native;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.Document.Saving;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;

namespace PdfEditor.Appearance.Tests;

/// <summary>
/// 다른 뷰어(Acrobat·PDF-XChange·Foxit·Chrome)에서 열어 볼 M2 호환성 샘플 `spikes/out/m2-annotations.pdf`를 만든다 (docs/checklists/viewer-compat.md).
/// 1쪽: 새 주석 7종 + 한글 메모가 든 영역 강조. 2쪽: /Rotate 90 + 스티커 메모·연필.
/// 3쪽: Acrobat식 원본(AnnotatedPdfFactory.CreateMixed)에서 사각형 이동·스티커 메모 내용 변경·연필 일부 지움.
/// 만든 뒤 PDFium으로 다시 열어 기본 사항만 자동 확인한다. 다른 뷰어에서의 모양은 사람이 확인한다(검증 필요).
/// </summary>
public class M2CompatSampleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-m2compat-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public M2CompatSampleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string OutDir([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "spikes", "out"));

    private static readonly PdfColor Red = new(229, 55, 45);
    private static readonly PdfColor Yellow = new(255, 235, 0);
    private static readonly PdfColor Blue = new(30, 90, 220);
    private static readonly PdfColor Green = new(20, 150, 70);

    private static Annotation Make(DocumentSession s, AnnotKind kind, PdfRectF rect, Func<Annotation, Annotation> f)
        => f(new Annotation
        {
            Id = s.Annotations.NewId(),
            Kind = kind,
            Rect = rect,
            Name = Guid.NewGuid().ToString("D"),
            Author = "M2 샘플",
            CreatedAt = new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.FromHours(9)),
            Flags = PdfAnnotFlags.Print,
        });

    private static Task Add(DocumentSession s, PageId page, Annotation a) => s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(page, a));

    [Fact]
    public async Task CreateM2AnnotationsSample()
    {
        // 3쪽짜리 바탕: 빈 A4 두 쪽 + Acrobat식 원본 문서의 쪽
        AnnotatedPdfFactory.CreateMixed(Path.Combine(_dir, "mixed.pdf"));
        var outDir = OutDir();
        Directory.CreateDirectory(outDir);
        var target = Path.Combine(outDir, "m2-annotations.pdf");

        await using (var s = await DocumentSession.OpenAsync(_t, Path.Combine(_dir, "mixed.pdf"), null))
        {
            await s.AnnotationsReady;
            var mixedSize = await _t.InvokeAsync(() => s.GetLoadedPage(s.Pages[0].Id).BoundsPt);
            float w = Math.Abs(mixedSize.Right - mixedSize.Left), h = Math.Abs(mixedSize.Top - mixedSize.Bottom);
            await s.Commands.ExecuteAsync(new InsertBlankPageCommand(0, w, h));
            await s.Commands.ExecuteAsync(new InsertBlankPageCommand(0, w, h));
            Assert.Equal(3, s.Pages.Length);
            var p1 = s.Pages[0].Id; var p2 = s.Pages[1].Id; var p3 = s.Pages[2].Id;
            await s.Commands.ExecuteAsync(new RotatePagesCommand([p2], 1));

            // ---- 1쪽: 새 주석 7종 + 영역 강조 ----
            await Add(s, p1, Make(s, AnnotKind.Ink, new PdfRectF(60, 700, 260, 780), a => a with
            {
                Color = Red, BorderWidth = 4, Contents = "연필 메모",
                InkStrokes = [[new PdfPointF(70, 710), new PdfPointF(110, 770), new PdfPointF(150, 720), new PdfPointF(200, 770), new PdfPointF(250, 715)]],
            }));
            await Add(s, p1, Make(s, AnnotKind.Highlight, new PdfRectF(60, 640, 300, 660), a => a with
            {
                Color = Yellow, Opacity = 0.5f, Contents = "형광펜(반투명)", Quads = [PdfQuadF.FromRect(new PdfRectF(60, 640, 300, 660))],
            }));
            await Add(s, p1, Make(s, AnnotKind.Underline, new PdfRectF(60, 600, 300, 620), a => a with
            {
                Color = Blue, Contents = "밑줄", Quads = [PdfQuadF.FromRect(new PdfRectF(60, 600, 300, 620))],
            }));
            await Add(s, p1, Make(s, AnnotKind.StrikeOut, new PdfRectF(60, 560, 300, 580), a => a with
            {
                Color = Red, Contents = "취소선", Quads = [PdfQuadF.FromRect(new PdfRectF(60, 560, 300, 580))],
            }));
            await Add(s, p1, Make(s, AnnotKind.Text, new PdfRectF(340, 700, 360, 720), a => a with
            {
                Color = Yellow, Contents = "스티커 메모: 도면 검토 필요 (한글 내용)",
            }));
            await Add(s, p1, Make(s, AnnotKind.Square, new PdfRectF(60, 430, 200, 520), a => a with
            {
                Color = Red, FillColor = Yellow, BorderWidth = 3, Opacity = 0.8f, Contents = "사각형",
            }));
            await Add(s, p1, Make(s, AnnotKind.Circle, new PdfRectF(240, 430, 380, 520), a => a with
            {
                Color = Blue, BorderWidth = 3, Contents = "원",
            }));
            await Add(s, p1, Make(s, AnnotKind.Highlight, new PdfRectF(60, 300, 380, 400), a => a with
            {
                Color = Green, Opacity = 0.4f, Contents = "영역 강조: 이 부분 치수 재확인",
                Quads = [PdfQuadF.FromRect(new PdfRectF(60, 300, 380, 400))],
            }));

            // ---- 2쪽: /Rotate 90 + 스티커 메모·연필 ----
            await Add(s, p2, Make(s, AnnotKind.Text, new PdfRectF(100, 700, 120, 720), a => a with { Color = Yellow, Contents = "회전한 쪽의 메모" }));
            await Add(s, p2, Make(s, AnnotKind.Ink, new PdfRectF(150, 500, 350, 600), a => a with
            {
                Color = Blue, BorderWidth = 3,
                InkStrokes = [[new PdfPointF(160, 510), new PdfPointF(200, 590), new PdfPointF(260, 520), new PdfPointF(340, 590)]],
            }));

            // ---- 3쪽: 원본 [1] 이동, [3] 내용 변경, [7] 일부 지움 ----
            var items = s.Annotations.Get(p3).Items;
            var square = items.First(i => i.Data.Kind == AnnotKind.Square).Data;
            var note = items.First(i => i.Data.Kind == AnnotKind.Text).Data;
            var ink = items.First(i => i.Data.Kind == AnnotKind.Ink).Data;
            var r = square.Rect;
            await s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(p3,
            [
                new AnnotationChange(square, square with { Rect = new PdfRectF(r.Left + 120, r.Bottom - 60, r.Right + 120, r.Top - 60) }),
            ]));
            await s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(p3,
            [
                new AnnotationChange(note, note with { Contents = "내용을 바꾼 메모 (M2)" }),
            ]));
            var erased = InkEraser.EraseCircle(ink.InkStrokes, new PdfPointF(130, 440), 8f);
            Assert.NotEqual(ink.InkStrokes.Length, erased.Length);
            await s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(p3, [new AnnotationChange(ink, ink with { InkStrokes = erased })]));

            await new SaveService([], new PdfAppearanceSource(), new PdfEditor.Appearance.Patching.PdfSharpAnnotationPatcher(() => [])).SaveAsync(s, target);

            // ---- 다시 열어 확인 (PDFium + 읽기) ----
            var bytes = File.ReadAllBytes(target);
            var back = AnnotationReader.Read(bytes, null, [0, 1, 2]);
            Assert.Equal(9, back[0].Count);   // 새 메모의 Popup 1개 포함 (M3a Task 3)
            Assert.All(back[0].Where(o => o.Subtype != PdfAnnotSubtype.Popup), o => Assert.Equal(ReadOnlyReason.None, o.Reason));
            Assert.Equal(3, back[1].Count);   // Text + Popup + Ink
            Assert.Contains(back[0], o => o.Data!.Contents == "영역 강조: 이 부분 치수 재확인");
            var movedSquare = back[2].First(o => o.Data?.Kind == AnnotKind.Square).Data!;
            Assert.Equal(r.Left + 120, movedSquare.Rect.Left, 0.5);
            Assert.Contains(back[2], o => o.Data?.Contents == "내용을 바꾼 메모 (M2)");
            Assert.Equal(erased.Length, back[2].First(o => o.Data?.Kind == AnnotKind.Ink).Data!.InkStrokes.Length);

            var rotations = await _t.InvokeAsync(() =>
            {
                using var doc = PdfDocument.Open(_t, target);
                var list = new List<int>();
                for (int i = 0; i < doc.PageCount; i++)
                {
                    using var page = doc.LoadPage(i);
                    list.Add(page.Rotation);
                    var size = new PageTransform(page.BoundsPt, page.Rotation, 1.0).ViewSize;
                    int pw = (int)Math.Ceiling(size.W), ph = (int)Math.Ceiling(size.H);
                    PngFile.WriteBgra(Path.Combine(outDir, $"m2-annotations-p{i + 1}.png"), page.RenderTile(1.0, 0, 0, pw, ph, withAnnotations: true), pw, ph);
                }
                return list;
            });
            Assert.Equal([0, 1, 0], rotations);   // PDFium 회전은 90도 단위 번호(1 = /Rotate 90)
        }
        Assert.True(File.Exists(target));
    }
}
