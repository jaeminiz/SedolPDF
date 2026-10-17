using System.Runtime.CompilerServices;
using Pdf.Native;
using PdfEditor.Appearance;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.Document.Commands;
using PdfEditor.Document.Saving;
using PdfEditor.Exchange.Summary;
using PdfEditor.Exchange.Xfdf;
using PdfEditor.TestFixtures;

namespace PdfEditor.Exchange.Tests;

/// <summary>
/// 다른 뷰어에서 열어 볼 M3b 호환성 샘플을 만든다 (docs/checklists/viewer-compat.md M3b 절).
/// `spikes/out/m3b-annotations.pdf` = M3a 샘플(`m3a-annotations.pdf`, 없으면 빈 4쪽) + 답글·검토 상태·스타일을 입힌 새 주석,
/// `m3b-annotations.xfdf` = 그 주석을 XFDF로 내보낸 것, `m3b-summary.pdf` = 주석 요약 PDF. 쪽 PNG도 `spikes/out`에 둔다.
/// 모두 실제 저장 길(PDFium Flush + PDFsharp 패치 단계)로 저장한 뒤 다시 열어 개수를 자동 확인한다.
/// 다른 뷰어에서의 모양은 사람이 확인한다(검증 필요). 스타일 적용 자체(AnnotationStylesViewModel)는 App 시험이 다루므로
/// 여기서는 스타일이 입힐 속성(색·두께·불투명도)을 직접 준 주석으로 저장·내보내기 길만 본다.
/// </summary>
[Collection("SummarySerial")]
public sealed class M3bAnnotationsSampleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-m3bcompat-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly AppFont _font = AppFont.Load(FontPath());
    private readonly AnnotationAssets _assets = new();

    public M3bAnnotationsSampleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        _font.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string FontPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var f = Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf");
            if (File.Exists(f)) return f;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("assets/fonts/NotoSansKR-Regular.otf");
    }

    private static string OutDir([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "spikes", "out"));

    private static readonly PdfColor Red = new(229, 55, 45);
    private static readonly PdfColor Green = new(20, 150, 70);
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 9, 0, 0, TimeSpan.FromHours(9));
    private const string StateTemplate = "{0} · 상태: {1}";

    private static Annotation FindFirst(DocumentSession s, int page, AnnotKind kind)
        => s.Annotations.Get(s.Pages[page].Id).Items.Select(v => v.Data).First(a => a.Kind == kind && a.InReplyTo is null);

    private static Task Add(DocumentSession s, int page, params Annotation[] items)
        => s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(s.Pages[page].Id, [.. items.Select(a => new AnnotationChange(null, a))]));

    [Fact]
    public async Task CreateM3bAnnotationsSample()
    {
        var outDir = OutDir();
        Directory.CreateDirectory(outDir);
        var target = Path.Combine(outDir, "m3b-annotations.pdf");
        var xfdfPath = Path.Combine(outDir, "m3b-annotations.xfdf");
        var summaryPath = Path.Combine(outDir, "m3b-summary.pdf");

        string baseFile = Path.Combine(_dir, "base.pdf");
        string m3a = Path.Combine(outDir, "m3a-annotations.pdf");
        bool haveM3a = File.Exists(m3a);
        if (haveM3a) File.Copy(m3a, baseFile, true);
        else TestPdfFactory.CreateBlank(baseFile, 4, 595, 842);

        int[] baseCounts = [.. AnnotationReader.Read(File.ReadAllBytes(baseFile), null, [0, 1, 2, 3]).Select(l => l.Count)];

        using var patcher = new PdfSharpAnnotationPatcher(_font);
        var svc = new SaveService([], new PdfAppearanceSource(_font, _assets), patcher, _assets);

        // 요약과 XFDF에 쓸 모델 (저장 전 세션에서 모은다)
        var exportList = new List<(int Page, Annotation A)>();
        await using (var s = await DocumentSession.OpenAsync(_t, baseFile, null))
        {
            await s.AnnotationsReady;
            if (!haveM3a)
            {
                // 빈 문서면 최소한의 부모 주석을 직접 만든다
                for (int p = 0; p < 2; p++)
                {
                    var a = new Annotation
                    {
                        Id = s.Annotations.NewId(), Kind = p == 0 ? AnnotKind.Line : AnnotKind.FreeText,
                        Rect = new PdfRectF(60, 700, 300, 760), Name = Guid.NewGuid().ToString("D"), Author = "M3b 샘플", Contents = "부모 " + p,
                        Vertices = p == 0 ? [new PdfPointF(60, 730), new PdfPointF(300, 730)] : [],
                        Intent = p == 1 ? FreeTextIntent.TextBox : default, FontSizePt = 14f, Color = Red, BorderWidth = 2, Flags = PdfAnnotFlags.Print,
                        CreatedAt = T0, ModifiedAt = T0,
                    };
                    await Add(s, p, a);
                }
            }

            // ---- 1쪽: 선에 답글(+ 답글의 답글)과 "승인" 상태. 스타일을 입힌 새 사각형(검토 빨강)·원(승인 초록) ----
            var line = FindFirst(s, 0, AnnotKind.Line);
            var r1 = ThreadAnnotations.NewReply(s.Annotations, line, "확인했습니다. 치수 기입이 맞습니다.", "박검토", T0.AddMinutes(10));
            await Add(s, 0, r1);
            var r2 = ThreadAnnotations.NewReply(s.Annotations, r1, "감사합니다. 가나다 도면 개정 반영 예정입니다. Thanks.", "김재민", T0.AddMinutes(20));
            await Add(s, 0, r2);
            var st1 = ThreadAnnotations.NewState(s.Annotations, line, ReviewState.Accepted, "박검토", T0.AddMinutes(30), StateTemplate, "승인");
            await Add(s, 0, st1);

            await Add(s, 0, new Annotation
            {
                Id = s.Annotations.NewId(), Kind = AnnotKind.Square, Rect = new PdfRectF(330, 100, 540, 200), Name = Guid.NewGuid().ToString("D"),
                Author = "스타일 샘플", Contents = "스타일 '검토 빨강' 적용: 빨강, 두께 3, 불투명도 0.8", Color = Red, BorderWidth = 3, Opacity = 0.8f,
                CreatedAt = T0.AddHours(1), ModifiedAt = T0.AddHours(1), Flags = PdfAnnotFlags.Print,
            });
            await Add(s, 0, new Annotation
            {
                Id = s.Annotations.NewId(), Kind = AnnotKind.Circle, Rect = new PdfRectF(60, 100, 260, 200), Name = Guid.NewGuid().ToString("D"),
                Author = "스타일 샘플", Contents = "스타일 '승인 초록' 적용", Color = Green, BorderWidth = 2, Opacity = 1f,
                CreatedAt = T0.AddHours(1), ModifiedAt = T0.AddHours(1), Flags = PdfAnnotFlags.Print,
            });

            // ---- 2쪽: 텍스트 상자에 한글 답글, 상태 "거부" → 나중에 "완료"(가장 최근 상태가 보임) ----
            var box = FindFirst(s, 1, AnnotKind.FreeText);
            var r3 = ThreadAnnotations.NewReply(s.Annotations, box, "이 부분은 다시 검토해 주세요. 줄바꿈\n둘째 줄.", "이검토", T0.AddMinutes(40));
            await Add(s, 1, r3);
            await Add(s, 1, ThreadAnnotations.NewState(s.Annotations, box, ReviewState.Rejected, "이검토", T0.AddMinutes(50), StateTemplate, "거부"));
            await Add(s, 1, ThreadAnnotations.NewState(s.Annotations, box, ReviewState.Completed, "김재민", T0.AddMinutes(60), StateTemplate, "완료"));
            Assert.Equal(ReviewState.Completed, s.Annotations.CurrentState(s.Pages[1].Id, box.Id));
            Assert.Equal(ReviewState.Accepted, s.Annotations.CurrentState(s.Pages[0].Id, line.Id));

            // 내보내기 모델 (저장 전 메모리 값)
            for (int i = 0; i < s.Pages.Length; i++)
                foreach (var v in s.Annotations.Get(s.Pages[i].Id).Items) exportList.Add((i, v.Data));

            await svc.SaveAsync(s, target);
        }

        // ---- 다시 열어 확인: 답글·상태·새 주석이 파일에 들어갔다 ----
        var bytes = File.ReadAllBytes(target);
        var back = AnnotationReader.Read(bytes, null, [0, 1, 2, 3]);
        if (haveM3a)
        {
            Assert.Equal(baseCounts[0] + 5, back[0].Count);   // 새 사각형·원 + 답글 2 + 상태 1
            Assert.Equal(baseCounts[1] + 3, back[1].Count);   // 답글 1 + 상태 2
            Assert.Equal(baseCounts[2], back[2].Count);
            Assert.Equal(baseCounts[3], back[3].Count);
        }
        Assert.Equal(3, back[0].Count(o => o.ReplyParentIndex is not null));   // 답글 2 + 상태 1
        Assert.Equal(3, back[1].Count(o => o.ReplyParentIndex is not null));   // 답글 1 + 상태 2
        Assert.Contains(back[0], o => o.Data is { State: ReviewState.Accepted });
        Assert.Contains(back[1], o => o.Data is { State: ReviewState.Rejected });
        Assert.Contains(back[1], o => o.Data is { State: ReviewState.Completed });
        Assert.Contains(back[0], o => o.Data?.Contents == "확인했습니다. 치수 기입이 맞습니다.");
        Assert.Contains(back[0], o => o.Data?.Kind == AnnotKind.Square && o.Data.Color == Red && Math.Abs(o.Data.BorderWidth - 3f) < 0.01f);

        // ---- XFDF 내보내기 (같은 모델) → 파일, 다시 읽기, 빈 문서로 가져오기 ----
        var xdoc = XfdfWriter.Write([.. exportList.Select(x => (x.Page, x.A))], "m3b-annotations.pdf", _assets);
        xdoc.Save(xfdfPath);
        int exportedElements = xdoc.Root!.Element(XfdfWriter.Ns + "annots")!.Elements().Count();
        Assert.Equal(exportList.Count, exportedElements);

        string blank = Path.Combine(_dir, "blank.pdf");
        TestPdfFactory.CreateBlank(blank, 4, 595, 842);
        await using (var s2 = await DocumentSession.OpenAsync(_t, blank, null))
        {
            await s2.AnnotationsReady;
            XfdfReadResult read;
            using (var fs = File.OpenRead(xfdfPath)) read = XfdfReader.Read(fs, s2.Annotations.NewId, _assets);
            var plan = XfdfImport.Plan(s2, read);
            Assert.NotNull(plan.Command);
            await s2.Commands.ExecuteAsync(plan.Command!);
            int imported = 0;
            for (int i = 0; i < s2.Pages.Length; i++) imported += s2.Annotations.Get(s2.Pages[i].Id).Items.Count();
            Assert.Equal(plan.Imported, imported);
            Assert.True(imported >= 6, "imported=" + imported);   // 답글·상태를 포함한 주석이 들어왔다
            Assert.Contains(s2.Annotations.Get(s2.Pages[0].Id).Items, v => v.Data.InReplyTo is not null);
        }

        // ---- 요약 PDF: 저장된 파일(패치 단계 주석 포함)을 PDFium으로 쪽마다 그려 만든다 ----
        var pages = new List<SummaryPage>();
        var rendered = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, target);
            var l = new List<(int Page, byte[] Bgra, int W, int H, PageTransform Tr)>();
            for (int i = 0; i < doc.PageCount; i++)
            {
                using var page = doc.LoadPage(i);
                var size = page.SizePt;
                double scale = 900.0 / Math.Max(1.0, Math.Max(size.Width, size.Height));
                var tr = new PageTransform(page.BoundsPt, page.Rotation, scale);
                var vs = tr.ViewSize;
                int w = Math.Max(1, (int)Math.Round(vs.W)), h = Math.Max(1, (int)Math.Round(vs.H));
                l.Add((i, page.RenderTile(scale, 0, 0, w, h, true), w, h, tr));
                var pw = (int)Math.Ceiling(size.Width); var ph = (int)Math.Ceiling(size.Height);
                PngFile.WriteBgra(Path.Combine(outDir, $"m3b-annotations-p{i + 1}.png"), page.RenderTile(1.0, 0, 0, pw, ph, withAnnotations: true), pw, ph);
            }
            return l;
        });
        foreach (var (pi, bgra, w, h, tr) in rendered)
        {
            var onPage = exportList.Where(x => x.Page == pi).Select(x => x.A).ToList();
            var tops = onPage.Where(a => a.InReplyTo is null).ToList();
            if (tops.Count == 0) continue;
            var entries = new List<SummaryEntry>();
            int n = 0;
            foreach (var a in tops)
            {
                n++;
                var r = a.Rect;
                var v = tr.ToView(System.Drawing.RectangleF.FromLTRB(r.Left, r.Bottom, r.Right, r.Top));
                var mark = new PdfRectF((float)v.X, (float)v.Y, (float)(v.X + v.W), (float)(v.Y + v.H));
                var replies = new List<SummaryEntry>();
                var queue = new Queue<AnnotId>([a.Id]);
                while (queue.Count > 0)
                {
                    var parent = queue.Dequeue();
                    foreach (var x in onPage.Where(x => x.InReplyTo == parent))
                    {
                        queue.Enqueue(x.Id);
                        string kindName = x.ReplyKind == ReplyType.Group ? "묶음" : x.State is { } st && st != ReviewState.None ? "상태" : "답글";
                        replies.Add(new SummaryEntry(0, kindName, x.Author, x.CreatedAt, x.Contents, [], mark));
                    }
                }
                entries.Add(new SummaryEntry(n, a.Kind.ToString(), a.Author, a.CreatedAt, a.Contents, replies, mark));
            }
            pages.Add(new SummaryPage(pi + 1, bgra, w, h, entries));
        }
        var ko = new SummaryText("만든 시각: {0}", "주석 {0}개", "{0}쪽", "{0}쪽 (이어서)", "글꼴에 없는 글자 {0}개: {1}");
        var result = SummaryPdfBuilder.Build(new SummaryInput("m3b-annotations.pdf", T0, pages) { Text = ko }, _font, summaryPath, CancellationToken.None);
        Assert.True(result.SummaryPageCount >= 1);
        Assert.Empty(result.MissingCodepoints);

        var summaryInfo = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, summaryPath);
            var texts = new List<string>();
            for (int i = 0; i < doc.PageCount; i++)
            {
                using var page = doc.LoadPage(i);
                using var tp = page.LoadText();
                texts.Add(tp.GetText(0, tp.CharCount));
                var size = page.SizePt;
                int pw = (int)Math.Ceiling(size.Width), ph = (int)Math.Ceiling(size.Height);
                PngFile.WriteBgra(Path.Combine(outDir, $"m3b-summary-p{i + 1}.png"), page.RenderTile(1.0, 0, 0, pw, ph, withAnnotations: true), pw, ph);
            }
            return texts;
        });
        string all = string.Concat(summaryInfo).Replace("\r", "").Replace("\n", "").Replace(" ", "");
        Assert.Contains("확인했습니다.치수기입이맞습니다.", all);
        Assert.Contains("이부분은다시검토해주세요.", all);
    }
}
