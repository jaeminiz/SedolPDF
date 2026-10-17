using System.Runtime.CompilerServices;
using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Appearance.Tests.Fonts;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.Document.Saving;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;

namespace PdfEditor.Appearance.Tests;

/// <summary>
/// 다른 뷰어(Acrobat·PDF-XChange·Foxit·Chrome)에서 열어 볼 M3a 호환성 샘플 `spikes/out/m3a-annotations.pdf`를 만든다 (docs/checklists/viewer-compat.md M3a 절).
/// 1쪽: 선 끝 8종·폴리라인·다각형·클라우드 2종. 2쪽: 텍스트 상자(한글·영문·줄바꿈)·타자기·설명선.
/// 3쪽: Caret·바꾸기 묶음·스탬프 3종(기본·글자·그림)·첨부 파일. 4쪽: /Rotate 90 + 메모(Popup)·텍스트 상자.
/// 모두 실제 저장 길(PDFium Flush + PDFsharp 패치 단계)로 저장한 뒤 다시 열어 개수·종류를 자동 확인한다.
/// 다른 뷰어에서의 모양은 사람이 확인한다(검증 필요).
/// </summary>
public class M3aCompatSampleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-m3acompat-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly AppFont _font = AppFont.Load(FontTestSupport.FontPath());
    private readonly AnnotationAssets _assets = new();

    public M3aCompatSampleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        _font.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string OutDir([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "spikes", "out"));

    private static readonly PdfColor Red = new(229, 55, 45);
    private static readonly PdfColor Yellow = new(255, 245, 157);
    private static readonly PdfColor Blue = new(30, 90, 220);
    private static readonly PdfColor Green = new(20, 150, 70);
    private static readonly PdfColor Black = new(0, 0, 0);

    private static Annotation Make(DocumentSession s, AnnotKind kind, PdfRectF rect, Func<Annotation, Annotation> f)
        => f(new Annotation
        {
            Id = s.Annotations.NewId(),
            Kind = kind,
            Rect = rect,
            Name = Guid.NewGuid().ToString("D"),
            Author = "M3a 샘플",
            CreatedAt = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.FromHours(9)),
            ModifiedAt = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.FromHours(9)),
            Flags = PdfAnnotFlags.Print,
        });

    private static Task Add(DocumentSession s, PageId page, Annotation a) => s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(page, a));

    private static Annotation WithComputedRect(Annotation a) => a with { Rect = AnnotationGeometry.ComputeRect(a) };

    [Fact]
    public async Task CreateM3aAnnotationsSample()
    {
        const float W = 595, H = 842;
        var outDir = OutDir();
        Directory.CreateDirectory(outDir);
        var target = Path.Combine(outDir, "m3a-annotations.pdf");
        string blank = Path.Combine(_dir, "blank.pdf");
        TestPdfFactory.CreateBlank(blank, 4, W, H);

        using var patcher = new PdfSharpAnnotationPatcher(_font);
        var svc = new SaveService([], new PdfAppearanceSource(_font, _assets), patcher, _assets);

        await using (var s = await DocumentSession.OpenAsync(_t, blank, null))
        {
            await s.AnnotationsReady;
            var p1 = s.Pages[0].Id; var p2 = s.Pages[1].Id; var p3 = s.Pages[2].Id; var p4 = s.Pages[3].Id;
            await s.Commands.ExecuteAsync(new RotatePagesCommand([p4], 1));

            // ---- 1쪽: 선 끝 8종, 폴리라인, 다각형, 클라우드 2종 ----
            LineEnding[] endings = [LineEnding.Square, LineEnding.Circle, LineEnding.Diamond, LineEnding.OpenArrow,
                                    LineEnding.ClosedArrow, LineEnding.Butt, LineEnding.Slash, LineEnding.None];
            for (int i = 0; i < endings.Length; i++)
            {
                float y = 800 - i * 26;
                var e = endings[i];
                await Add(s, p1, WithComputedRect(Make(s, AnnotKind.Line, default, a => a with
                {
                    Color = i % 2 == 0 ? Red : Blue, FillColor = Yellow, BorderWidth = 2.5f, Contents = "선 끝 " + e,
                    Vertices = [new PdfPointF(60, y), new PdfPointF(300, y)], StartEnding = e, EndEnding = e,
                })));
            }
            await Add(s, p1, WithComputedRect(Make(s, AnnotKind.PolyLine, default, a => a with
            {
                Color = Green, BorderWidth = 2.5f, StartEnding = LineEnding.Diamond, EndEnding = LineEnding.OpenArrow, Contents = "폴리라인",
                Vertices = [new PdfPointF(340, 800), new PdfPointF(400, 700), new PdfPointF(460, 790), new PdfPointF(540, 690)],
            })));
            await Add(s, p1, WithComputedRect(Make(s, AnnotKind.Polygon, default, a => a with
            {
                Color = Red, FillColor = Yellow, BorderWidth = 2, Opacity = 0.7f, Contents = "다각형",
                Vertices = [new PdfPointF(60, 540), new PdfPointF(200, 580), new PdfPointF(260, 500), new PdfPointF(120, 470)],
            })));
            await Add(s, p1, Make(s, AnnotKind.Square, new PdfRectF(320, 470, 540, 600), a => a with
            {
                Color = Red, BorderWidth = 2, CloudIntensity = 1f, Contents = "사각형 클라우드",
            }));
            await Add(s, p1, WithComputedRect(Make(s, AnnotKind.Polygon, default, a => a with
            {
                Color = Blue, FillColor = Yellow, BorderWidth = 2, CloudIntensity = 1.5f, Opacity = 0.8f, Contents = "다각형 클라우드",
                Vertices = [new PdfPointF(80, 360), new PdfPointF(260, 380), new PdfPointF(300, 260), new PdfPointF(170, 220), new PdfPointF(70, 270)],
            })));

            // ---- 2쪽: 텍스트 상자(한글·영문·줄바꿈), 타자기, 설명선 ----
            Annotation FreeBox(PdfRectF rect, string text, Func<Annotation, Annotation>? f = null) => Make(s, AnnotKind.FreeText, rect, a =>
            {
                a = a with
                {
                    Intent = FreeTextIntent.TextBox, Contents = text, FontSizePt = 14f, TextColor = Black, Color = Red, FillColor = Yellow,
                    BorderWidth = 1.5f, Subject = "textbox",
                };
                return f is null ? a : f(a);
            });
            await Add(s, p2, FreeBox(new PdfRectF(50, 700, 330, 760), "가나다 도면 검토 ABC"));
            await Add(s, p2, FreeBox(new PdfRectF(50, 630, 330, 680), "English text box: Check the drawing dimensions.", a => a with { Color = Blue }));
            await Add(s, p2, FreeBox(new PdfRectF(50, 520, 200, 610),
                "줄바꿈 시험: 가나다라마바사아자차카타파하 도면 검토 필요 ABC DEF 123\n둘째 줄입니다.", a => a with { Align = TextAlign.Center, FillColor = null }));
            var tw = Make(s, AnnotKind.FreeText, new PdfRectF(50, 470, 330, 500), a => a with
            {
                Intent = FreeTextIntent.Typewriter, Contents = "타자기: 가나다 ABC 2026", FontSizePt = 16f, TextColor = Red, Color = null, FillColor = null,
                BorderWidth = 0, Subject = "typewriter",
            });
            tw = tw with { Rect = AnnotationPainter.FitTypewriter(tw, new PaintContext(_font, _assets)).Rect };
            await Add(s, p2, tw);
            var box = new PdfRectF(300, 300, 520, 370);
            await Add(s, p2, WithComputedRect(Make(s, AnnotKind.FreeText, box, a => a with
            {
                Intent = FreeTextIntent.Callout, Contents = "설명선: 이 부분 확인", FontSizePt = 14f, TextColor = Black, Color = Red, FillColor = Yellow,
                BorderWidth = 1.5f, TextBox = box, EndEnding = LineEnding.OpenArrow, Subject = "callout",
                Vertices = [new PdfPointF(100, 250), new PdfPointF(210, 320), new PdfPointF(300, 335)],
            })));

            // ---- 3쪽: Caret, 바꾸기 묶음, 스탬프 3종, 첨부 ----
            await Add(s, p3, Make(s, AnnotKind.Caret, new PdfRectF(60, 770, 80, 795), a => a with { Color = Red, Contents = "여기에 삽입: 가나다" }));
            var strike = Make(s, AnnotKind.StrikeOut, new PdfRectF(60, 700, 300, 720), a => a with
            {
                Color = Red, Contents = "바꾸기: 원래 글", Name = null,
                Quads = [PdfQuadF.FromRect(new PdfRectF(60, 700, 300, 720))],
            });
            var replaceCaret = Make(s, AnnotKind.Caret, new PdfRectF(296, 696, 314, 722), a => a with
            {
                Color = Blue, Contents = "새 글 NEW", InReplyTo = strike.Id, ReplyKind = ReplyType.Group,
            });
            await s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(p3, [new AnnotationChange(null, strike), new AnnotationChange(null, replaceCaret)]));
            await Add(s, p3, Make(s, AnnotKind.Stamp, new PdfRectF(60, 600, 210, 650), a => a with
            {
                Color = Green, Stamp = new StampSpec(StampKind.Builtin, "Approved", "승인 Approved", null), Subject = "stamp-builtin",
            }));
            await Add(s, p3, Make(s, AnnotKind.Stamp, new PdfRectF(240, 600, 440, 650), a => a with
            {
                Color = Red, Stamp = new StampSpec(StampKind.Text, "", "검토 완료 REVIEWED", null), Subject = "stamp-text",
            }));
            string imgId = _assets.AddImage(TestImages.GradientWithHole());
            await Add(s, p3, Make(s, AnnotKind.Stamp, new PdfRectF(60, 500, 260, 580), a => a with
            {
                Stamp = new StampSpec(StampKind.Image, "", null, imgId), Subject = "stamp-image",
            }));
            var data = new byte[3000];
            new Random(11).NextBytes(data);
            string fileId = _assets.AddFile("검토 메모.txt", data);
            await Add(s, p3, Make(s, AnnotKind.FileAttachment, new PdfRectF(300, 520, 324, 544), a => a with
            {
                Color = Blue, AttachmentId = fileId, AttachmentName = "검토 메모.txt", Contents = "첨부 파일",
            }));

            // ---- 4쪽: /Rotate 90 + 메모(Popup) + 텍스트 상자 ----
            await Add(s, p4, Make(s, AnnotKind.Text, new PdfRectF(100, 700, 120, 720), a => a with
            {
                Color = Yellow, Contents = "회전한 쪽의 메모: 아이콘이 똑바로 서 있어야 함",
            }));
            await Add(s, p4, FreeBox(new PdfRectF(150, 500, 420, 570), "회전한 쪽의 텍스트 상자: 가나다 ABC"));

            await svc.SaveAsync(s, target);
        }

        // ---- 다시 열어 확인 (읽기 + PDFium) ----
        var bytes = File.ReadAllBytes(target);
        var back = AnnotationReader.Read(bytes, null, [0, 1, 2, 3]);
        static int Count(IReadOnlyList<OriginalAnnotation> l, AnnotKind k) => l.Count(o => o.Data?.Kind == k);
        Assert.Equal(8, Count(back[0], AnnotKind.Line));
        Assert.Equal(1, Count(back[0], AnnotKind.PolyLine));
        Assert.Equal(3, Count(back[0], AnnotKind.Polygon) + Count(back[0], AnnotKind.Square));
        Assert.Contains(back[0], o => o.Data?.Kind == AnnotKind.Square && o.Data.CloudIntensity > 0);
        Assert.Contains(back[0], o => o.Data?.Kind == AnnotKind.Polygon && o.Data.CloudIntensity > 0);
        Assert.Equal(12, back[0].Count);
        Assert.All(back[0], o => Assert.Equal(ReadOnlyReason.None, o.Reason));

        Assert.Equal(5, Count(back[1], AnnotKind.FreeText));
        Assert.Contains(back[1], o => o.Data?.Contents == "가나다 도면 검토 ABC");
        Assert.Contains(back[1], o => o.Data?.Intent == FreeTextIntent.Typewriter);
        Assert.Contains(back[1], o => o.Data?.Intent == FreeTextIntent.Callout && o.Data.Vertices.Length == 3);
        Assert.All(back[1], o => Assert.Equal(ReadOnlyReason.None, o.Reason));

        Assert.Equal(2, Count(back[2], AnnotKind.Caret));
        Assert.Equal(1, Count(back[2], AnnotKind.StrikeOut));
        Assert.Contains(back[2], o => o.Data?.Kind == AnnotKind.Caret && o.GroupParentIndex == 0);   // /IRT + /RT /Group 로 StrikeOut(0)에 묶임
        Assert.Equal(0, back[2].Count(o => o.Data?.Kind == AnnotKind.Stamp && o.Reason == ReadOnlyReason.None));   // 원본 Stamp 읽기는 보기 전용
        Assert.Equal(3, back[2].Count(o => o.Subtype == PdfAnnotSubtype.Stamp));
        Assert.Equal(1, back[2].Count(o => o.Subtype == PdfAnnotSubtype.FileAttachment));
        Assert.NotNull(AnnotationReader.ReadAttachment(bytes, null, 2, back[2].ToList().FindIndex(o => o.Subtype == PdfAnnotSubtype.FileAttachment)));

        Assert.Equal(1, Count(back[3], AnnotKind.Text));
        Assert.Equal(1, Count(back[3], AnnotKind.FreeText));
        Assert.Contains(back[3], o => o.Subtype == PdfAnnotSubtype.Popup);   // 새 메모에는 Popup이 붙는다

        var rotations = await _t.InvokeAsync(() =>
        {
            using var doc = Pdf.Native.PdfDocument.Open(_t, target);
            var list = new List<int>();
            for (int i = 0; i < doc.PageCount; i++)
            {
                using var page = doc.LoadPage(i);
                list.Add(page.Rotation);
                var size = new PageTransform(page.BoundsPt, page.Rotation, 1.0).ViewSize;
                int pw = (int)Math.Ceiling(size.W), ph = (int)Math.Ceiling(size.H);
                PngFile.WriteBgra(Path.Combine(outDir, $"m3a-annotations-p{i + 1}.png"), page.RenderTile(1.0, 0, 0, pw, ph, withAnnotations: true), pw, ph);
            }
            return list;
        });
        Assert.Equal([0, 0, 0, 1], rotations);
        Assert.True(File.Exists(target));
    }
}
