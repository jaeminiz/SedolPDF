using Pdf.Native;
using PdfEditor.Document.Content;
using PdfEditor.Document.Output;
using PdfEditor.Document.Recovery;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Document.Tests.Output;

/// <summary>M4 Task 4: 출력 단계 한 곳(OutputCopyBuilder)과 객체 편집 확정(내용 단계).</summary>
public sealed class OutputCopyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-output-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public OutputCopyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);

    private async Task<DocumentSession> Open(string? password = null)
    {
        string p = P(Guid.NewGuid().ToString("N") + ".pdf");
        ContentPdfFactory.CreateDocumentLike(p);
        if (password is not null)
        {
            using var doc = PdfReader.Open(p, PdfDocumentOpenMode.Modify);
            doc.SecuritySettings.UserPassword = password;
            doc.SecuritySettings.OwnerPassword = password + "-owner";
            doc.Save(p);
        }
        return await DocumentSession.OpenAsync(_t, p, password);
    }

    /// <summary>이미지 지우기 + 사각형 50pt 오른쪽으로.</summary>
    private Task<ContentEditCommand> Edits(DocumentSession s) => _t.InvokeAsync(() =>
    {
        var id = s.Pages[0].Id;
        var idx = s.GetObjectIndex(id);
        int img = idx.Entries.First(e => e.Type == PdfPageObjectType.Image).Ref.OriginalIndex;
        int rect = idx.Entries.Where(e => e.Type == PdfPageObjectType.Path).OrderBy(e => e.Bounds.Height).Last().Ref.OriginalIndex;
        return new ContentEditCommand(id,
        [
            new DeactivateEdit(s.Content.NewId(), ObjectRef.Original(img)),
            new TransformEdit(s.Content.NewId(), ObjectRef.Original(rect), PdfMatrix.Translate(50, 0)),
        ]);
    });

    private (int Images, float RectLeft, byte[] Render) Inspect(string path, string? password = null) => _t.InvokeAsync(() =>
    {
        using var doc = PdfDocument.Open(_t, path, password);
        using var page = doc.LoadPage(0);
        var objs = Enumerable.Range(0, page.ObjectCount).Select(page.GetObject).ToList();
        float rectLeft = objs.Where(o => o.Type == PdfPageObjectType.Path).OrderBy(o => o.Bounds.Height).Last().Bounds.Left;
        var px = page.RenderTile(0.5, 0, 0, 298, 421, false);
        return (objs.Count(o => o.Type == PdfPageObjectType.Image), rectLeft, px);
    }).GetAwaiter().GetResult();

    private static byte[] ContentBytes(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var c = doc.Pages[0].Contents;
        return c.Elements.Cast<PdfSharp.Pdf.PdfItem>().SelectMany(it =>
            (it as PdfSharp.Pdf.Advanced.PdfReference)?.Value is PdfSharp.Pdf.PdfDictionary d && d.Stream is { } st ? st.UnfilteredValue : []).ToArray();
    }

    [Fact]
    public async Task Save_AppliesEdits_SaveThreeTimes_SameContent()
    {
        await using var s = await Open();
        await s.Commands.ExecuteAsync(await Edits(s));
        var svc = new SaveService([]);
        var outs = new[] { P("a.pdf"), P("b.pdf"), P("c.pdf") };
        foreach (var o in outs) await svc.SaveAsync(s, o);
        var (images, rectLeft, _) = Inspect(outs[0]);
        Assert.Equal(0, images);
        Assert.Equal(260 + 50 - 1, rectLeft, 1.5f);   // 선 두께 2pt의 절반만큼 왼쪽
        var c0 = ContentBytes(outs[0]);
        Assert.Equal(c0, ContentBytes(outs[1]));
        Assert.Equal(c0, ContentBytes(outs[2]));
    }

    [Fact]
    public async Task EditSaveUndoSave_OriginalContentBack()
    {
        await using var s = await Open();
        var original = Inspect(s.FilePath!);
        await s.Commands.ExecuteAsync(await Edits(s));
        await new SaveService([]).SaveAsync(s, P("edited.pdf"));
        await s.Commands.UndoAsync();
        await new SaveService([]).SaveAsync(s, P("undone.pdf"));
        var back = Inspect(P("undone.pdf"));
        Assert.Equal(original.Images, back.Images);
        Assert.Equal(original.RectLeft, back.RectLeft, 0.5f);
        Assert.Equal(original.Render, back.Render);
    }

    [Fact]
    public async Task LiveDocument_NeverGenerated_AfterSave()
    {
        await using var s = await Open();
        await s.Commands.ExecuteAsync(await Edits(s));
        await new SaveService([]).SaveAsync(s, P("x.pdf"));
        // 저장한 뒤에도 편집 중인 문서의 쪽을 새로 열면 원본 그대로(이미지 있음) — 편집은 replay로만 보인다
        int images = await _t.InvokeAsync(() =>
        {
            using var page = s.Document.LoadPage(0);
            return Enumerable.Range(0, page.ObjectCount).Count(i => page.GetObject(i).Type == PdfPageObjectType.Image);
        });
        Assert.Equal(1, images);
    }

    [Fact]
    public async Task EditedPage_UntouchedRegion_PixelsUnchanged()
    {
        await using var s = await Open();
        var before = Inspect(s.FilePath!).Render;
        await s.Commands.ExecuteAsync(await Edits(s));
        await new SaveService([]).SaveAsync(s, P("e.pdf"));
        var after = Inspect(P("e.pdf")).Render;
        // 이미지(60~180, 200~290)·사각형(260~460, 200~290) 띠(y 190~300pt) 밖은 그대로 (0.5배 렌더 → 픽셀 y 95~150 밖)
        int diff = 0;
        for (int y = 0; y < 421; y++)
        {
            if (y is >= 93 and <= 152) continue;
            for (int x = 0; x < 298; x++)
            {
                int o = (y * 298 + x) * 4;
                if (Math.Abs(before[o] - after[o]) > 8 || Math.Abs(before[o + 1] - after[o + 1]) > 8 || Math.Abs(before[o + 2] - after[o + 2]) > 8) diff++;
            }
        }
        Assert.Equal(0, diff);
    }

    [Fact]
    public async Task Encrypted_ContentEdits_SavedStillEncrypted()
    {
        await using var s = await Open("pw1");
        await s.Commands.ExecuteAsync(await Edits(s));
        await new SaveService([]).SaveAsync(s, P("enc.pdf"));
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => _t.InvokeAsync(() => { using var d = PdfDocument.Open(_t, P("enc.pdf")); return 0; }));
        Assert.Equal(0, Inspect(P("enc.pdf"), "pw1").Images);
    }

    [Fact]
    public async Task Recovery_ContainsEdits()
    {
        await using var s = await Open();
        await s.Commands.ExecuteAsync(await Edits(s));
        var store = new RecoveryStore(P("rec"));
        await store.WriteAsync(s);
        var entry = Assert.Single(store.List());
        Assert.Equal(0, Inspect(entry.RecoveryPdfPath).Images);
    }

    [Fact]
    public async Task BuildIfNeeded_NoEditsNoPatch_ReturnsNull_WithEdits_WritesCopy()
    {
        await using var s = await Open();
        var b = new OutputCopyBuilder(null, null, null);
        Assert.Null(await b.BuildIfNeededAsync(s, P("none.pdf")));   // 인쇄·요약은 살아 있는 문서를 그대로 쓴다(OnlyAnnotations_NoContentStage)
        Assert.False(File.Exists(P("none.pdf")));
        await s.Commands.ExecuteAsync(await Edits(s));
        var copy = await b.BuildIfNeededAsync(s, P("some.pdf"));   // Print_ShowsMovedImage·Summary_PageImageShowsEdits가 쓰는 사본
        Assert.NotNull(copy);
        Assert.True(copy!.ContentApplied);
        Assert.Equal(0, Inspect(P("some.pdf")).Images);
    }

    /// <summary>
    /// 쪽 내용이 여러 스트림이고 경로 하나가 스트림 경계에 걸침(첫 스트림 끝 "q 0 0 1 RG 3 w", 둘째 스트림에서 "m l S Q").
    /// 2026-11-10 사용자 PDF(약 200KB씩 나뉜 도면): 첫 스트림의 객체만 편집하면 PDFium이 그 스트림만 다시 쓰며 걸친 q를 빠뜨려
    /// 둘째 스트림에 짝 없는 Q가 남았다. Acrobat은 거기서 오류로 보고 쪽의 나머지를 그리지 않는다(PDFium·PDF-XChange는 넘어감).
    /// </summary>
    private string CreateStraddlingStreams()
    {
        string p = P(Guid.NewGuid().ToString("N") + ".pdf");
        using (var doc = new PdfSharp.Pdf.PdfDocument())
        {
            var page = doc.AddPage();
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(595);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(842);
            page.Contents.Elements.Clear();
            foreach (var s in new[]
            {
                "q 1 0 0 RG 2 w 50 100 m 250 100 l S Q\nq 0 0 1 RG 3 w\n",
                "50 400 m 400 400 l S Q\nq 0 1 0 rg 300 600 80 80 re f Q\n",
                "q 0 0 0 rg 100 700 40 40 re f Q\n",
            })
                page.Contents.AppendContent().CreateStream(System.Text.Encoding.ASCII.GetBytes(s));
            doc.Save(p);
        }
        return p;
    }

    /// <summary>모든 내용 스트림을 이어 붙인 연산자 열에서 q/Q가 한 번도 음수로 내려가지 않고 끝에 0인가.</summary>
    private static void AssertQBalanced(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var parts = doc.Pages[0].Contents.Elements.Cast<PdfSharp.Pdf.PdfItem>()
            .Select(it => (it as PdfSharp.Pdf.Advanced.PdfReference)?.Value is PdfSharp.Pdf.PdfDictionary d && d.Stream is { } st ? st.UnfilteredValue : [])
            .Select(b => System.Text.Encoding.Latin1.GetString(b));
        var lexed = PdfEditor.ContentStream.ContentLexer.Parse(System.Text.Encoding.Latin1.GetBytes(string.Join("\n", parts)));
        Assert.True(lexed.Ok, lexed.Failure);
        int depth = 0;
        foreach (var op in lexed.Ops)
        {
            if (op.Name == "q") depth++;
            else if (op.Name == "Q") { depth--; Assert.True(depth >= 0, "unmatched Q (Acrobat stops drawing the page here)"); }
        }
        Assert.Equal(0, depth);
    }

    [Fact]
    public async Task Save_ObjectEditOnPageWithStraddlingStreams_KeepsQBalanced_AndLook()
    {
        string src = CreateStraddlingStreams();
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        var cmd = await _t.InvokeAsync(() =>
        {
            var id = s.Pages[0].Id;
            // 첫 스트림 안에서 끝나는 빨간 선(원본 0번)만 옮긴다
            return new ContentEditCommand(id, [new TransformEdit(s.Content.NewId(), ObjectRef.Original(0), PdfMatrix.Translate(0, 20))]);
        });
        await s.Commands.ExecuteAsync(cmd);
        await new SaveService([]).SaveAsync(s, P("straddle.pdf"));

        AssertQBalanced(P("straddle.pdf"));
        // 걸친 파란 선(3pt)도 그대로 그려진다: (225, 400) 근처가 파랗다(PDFium 렌더, 0.5배 → 픽셀 (112, 221))
        var px = await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, P("straddle.pdf"), null);
            using var page = doc.LoadPage(0);
            return page.RenderTile(0.5, 0, 0, 298, 421, false);
        });
        int i = (221 * 298 + 112) * 4;
        Assert.True(px[i] > 150 && px[i + 2] < 100, $"straddling blue line missing: BGR {px[i]},{px[i + 1]},{px[i + 2]}");
    }

    [Fact]
    public async Task Save_FileWithUnmatchedRestore_FromEarlierVersion_IsRepaired()
    {
        // 0.8.0까지의 저장이 만든 모양: 앞 스트림이 걸친 q를 잃어 뒤 스트림에 짝 없는 Q
        string src = P(Guid.NewGuid().ToString("N") + ".pdf");
        using (var doc = new PdfSharp.Pdf.PdfDocument())
        {
            var page = doc.AddPage();
            page.Contents.Elements.Clear();
            foreach (var c in new[] { "q 1 0 0 RG 2 w 50 100 m 250 100 l S Q\n", "0 0 1 RG 3 w 50 400 m 400 400 l S Q\nq 0 1 0 rg 300 600 80 80 re f Q\n" })
                page.Contents.AppendContent().CreateStream(System.Text.Encoding.ASCII.GetBytes(c));
            doc.Save(src);
        }
        await using var s = await DocumentSession.OpenAsync(_t, src, null);
        await new SaveService([], new PdfEditor.Appearance.PdfAppearanceSource(), new PdfEditor.Appearance.Patching.PdfSharpAnnotationPatcher(() => [])).SaveAsync(s, P("repaired.pdf"));
        AssertQBalanced(P("repaired.pdf"));
    }
}
