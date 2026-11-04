using Pdf.Native;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;

namespace PdfEditor.TextEdit.Tests;

/// <summary>0.6.0 글자 형식: 읽기(글꼴 이름·크기·색·굵게), 색 바꾸기(SetTextColorEdit, 저장 후에도 유지), 굵게·기울임 바꾸기(앱 글꼴로 다시 쓰기).</summary>
public sealed class TextFormatTests : IDisposable
{
    private static readonly Lazy<AppFont> Font = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"))) dir = dir.Parent;
        return AppFont.Load(Path.Combine(dir!.FullName, "assets", "fonts", "NotoSansKR-Regular.otf"));
    });

    private readonly PdfiumThread _t = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v060-format-" + Guid.NewGuid().ToString("N"));
    private readonly List<DocumentSession> _sessions = [];

    public void Dispose()
    {
        foreach (var s in _sessions) s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<DocumentSession> OpenNew(Action<string> create, string name = "f.pdf")
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, name);
        create(path);
        var s = await DocumentSession.OpenAsync(_t, path, null);
        _sessions.Add(s);
        return s;
    }

    private Task<PageObjectEntry> ArialLine(DocumentSession s) => _t.InvokeAsync(() =>
        s.GetObjectIndex(s.Pages[0].Id).Entries.First(e => e.TextPreview == ContentPdfFactory.LineArial));

    private Task<TextFormatInfo?> ReadAsync(DocumentSession s, ObjectRef r, bool overlapped = false) =>
        _t.InvokeAsync(() => TextFormatReader.Read(s, s.Pages[0].Id, r, overlapped));

    [Fact]
    public async Task Read_OriginalArialLine_SizeColorFont()
    {
        var s = await OpenNew(p => ContentPdfFactory.CreateDocumentLike(p));
        var line = await ArialLine(s);
        var f = await ReadAsync(s, line.Ref);
        Assert.NotNull(f);
        Assert.Equal(14f, f!.SizePt, 0.1f);
        Assert.Equal(new PdfColor(0, 0, 0), f.Color);
        Assert.Contains("Arial", f.FontName, StringComparison.OrdinalIgnoreCase);
        Assert.True(f.IsOriginalFont);
        Assert.False(f.Bold);
    }

    [Fact]
    public async Task Read_OverlappedCopy_IsBold()
    {
        var s = await OpenNew(p => ContentPdfFactory.CreateFauxBoldLine(p));
        var e = await _t.InvokeAsync(() => s.GetObjectIndex(s.Pages[0].Id).Entries.First(x => x.TextPreview == ContentPdfFactory.LineFauxBold));
        Assert.False((await ReadAsync(s, e.Ref))!.Bold);
        Assert.True((await ReadAsync(s, e.Ref, overlapped: true))!.Bold);
    }

    [Fact]
    public async Task Read_AfterScalingObject_ReportsVisibleSize()
    {
        var s = await OpenNew(p => ContentPdfFactory.CreateDocumentLike(p));
        var line = await ArialLine(s);
        var m = await _t.InvokeAsync(() => s.GetReplay(s.Pages[0].Id).Resolve(line.Ref)!.Matrix);
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, [new TransformEdit(s.Content.NewId(), line.Ref, PdfMatrix.ScaleAbout(2, 2, m.E, m.F))]));
        Assert.Equal(28f, (await ReadAsync(s, line.Ref))!.SizePt, 0.1f);
    }

    [Fact]
    public async Task SetTextColor_ChangesColorKeepsFont_AndSurvivesSave()
    {
        var s = await OpenNew(p => ContentPdfFactory.CreateDocumentLike(p));
        var line = await ArialLine(s);
        var red = new PdfColor(200, 20, 30);
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, [new SetTextColorEdit(s.Content.NewId(), line.Ref, red)]));
        var f = await ReadAsync(s, line.Ref);
        Assert.Equal(red, f!.Color);
        Assert.Contains("Arial", f.FontName, StringComparison.OrdinalIgnoreCase);   // 글꼴은 그대로

        // 저장 후 다시 열어도 색이 유지된다
        string saved = Path.Combine(_dir, "saved.pdf");
        var svc = new SaveService([], null, new PdfSharpAnnotationPatcher(Font.Value), s.Assets);
        await svc.SaveAsync(s, saved);
        var s2 = await DocumentSession.OpenAsync(_t, saved, null);
        _sessions.Add(s2);
        var f2 = await _t.InvokeAsync(() =>
        {
            var e = s2.GetObjectIndex(s2.Pages[0].Id).Entries.First(x => x.TextPreview == ContentPdfFactory.LineArial);
            return TextFormatReader.Read(s2, s2.Pages[0].Id, e.Ref);
        });
        Assert.Equal(red, f2!.Color);
    }

    [Fact]
    public async Task PlanReplace_BoldOverride_RewritesWithAppFontBold()
    {
        var s = await OpenNew(p => ContentPdfFactory.CreateDocumentLike(p));
        var line = await ArialLine(s);
        var plan = await _t.InvokeAsync(() => TextEditPlanner.PlanReplace(s, s.Pages[0].Id, line.Ref, ContentPdfFactory.LineArial, Font.Value,
            format: new TextFormatOverride(Bold: true)));
        var app = Assert.IsType<InsertAppTextEdit>(plan.Edits[0]);
        Assert.True(app.Line.BoldStrokePt > 0);
        Assert.True(plan.FallbackUsed);
    }

    [Fact]
    public async Task PlanReplace_ItalicOnAppLine_KeepsExistingBold()
    {
        var s = await OpenNew(p => ContentPdfFactory.CreateDocumentLike(p));
        var line = await ArialLine(s);
        var first = await _t.InvokeAsync(() => TextEditPlanner.PlanReplace(s, s.Pages[0].Id, line.Ref, ContentPdfFactory.LineArial, Font.Value, format: new TextFormatOverride(Bold: true)));
        await s.Commands.ExecuteAsync(new ContentEditCommand(s.Pages[0].Id, first.Edits));
        var appRef = ObjectRef.Inserted(first.Edits.OfType<InsertAppTextEdit>().Single().Id);
        var second = await _t.InvokeAsync(() => TextEditPlanner.PlanReplace(s, s.Pages[0].Id, appRef, ContentPdfFactory.LineArial, Font.Value, format: new TextFormatOverride(Italic: true)));
        var app = second.Edits.OfType<InsertAppTextEdit>().Single();
        Assert.True(app.Line.BoldStrokePt > 0);   // 굵게 유지
        Assert.True(app.Line.SyntheticItalic);
    }
}
