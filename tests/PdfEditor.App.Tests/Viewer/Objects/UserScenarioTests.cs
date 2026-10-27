using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Tests.Viewer.Annotations;
using PdfEditor.App.Resources;
using PdfEditor.App.Viewer;
using PdfEditor.App.Viewer.Objects;
using PdfEditor.Document;
using PdfEditor.Document.Content;
using PdfEditor.Document.Saving;
using PdfEditor.Pages;
using PdfEditor.TextEdit.Paragraphs;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;
using Xunit.Abstractions;

namespace PdfEditor.App.Tests.Viewer.Objects;

/// <summary>
/// M5 사용자 검수 안내서(docs/checklists/m5-user-test-guide.md) 1~6번을 헤드리스 합성 입력으로 자동 재현하는 시험.
/// 샘플은 spikes/out/m5-user-test/*.pdf(안내서와 같은 파일)를 실제로 연다 — 파일이 없는 PC(spikes/out은 저장소에 없음)에서는 시험을 건너뛴다(조용히 통과).
/// 실제 한글 IME·실제 마우스 감각·다른 뷰어·실제 Windows 125/150 % 배율은 자동으로 못 잡는다(사용자 최소 점검 목록 참고).
/// "KnownIssue_*" 시험은 사용자가 스크린샷으로 알려 온 문제를 <b>현재 상태 그대로</b> 고정한다: 고치면 이 시험이 실패하므로 그때 기대를 뒤집는다.
/// </summary>
[Collection("TabSettings")]
public class UserScenarioTests(ITestOutputHelper log) : ObjectUiTestBase
{
    private const string Pw = "test1234";

    private static readonly string? SampleDir = FindSampleDir();

    private static string? FindSampleDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string p = Path.Combine(dir.FullName, "spikes", "out", "m5-user-test");
            if (File.Exists(Path.Combine(p, "01-ime.pdf"))) return p;
            dir = dir.Parent;
        }
        return null;
    }

    private async Task<Host?> OpenUser(string name, string? pw = null, double width = 900)
    {
        if (SampleDir is null) return null;
        var s = await OpenAsync(p => File.Copy(Path.Combine(SampleDir, name + ".pdf"), p), pw);
        return Show(s, width);
    }

    // ---------------------------------------------------------------- 도우미

    /// <summary>글 객체 중 미리보기가 startsWith로 시작하는 첫 객체의 보이는 위치(보기 좌표계, 위에서부터 pt). rotation = 쪽의 시계 방향 회전(0·90).</summary>
    private async Task<(PageObjectEntry E, double X, double Y)> FindLine(Host h, string startsWith, int rotation = 0, int occurrence = 0)
    {
        var s = h.Session;
        var page = s.Pages[0].Id;
        var list = await Thread.InvokeAsync(() => s.GetObjectIndex(page).Entries
            .Where(e => e.Type == PdfPageObjectType.Text && e.TextPreview is { } t && t.StartsWith(startsWith, StringComparison.Ordinal)).ToList());
        var e = list[occurrence];
        double pageH = rotation == 0 ? h.PageRect.Height / K : h.PageRect.Width / K;
        double cx = e.Bounds.Left + 12, cy = (e.Bounds.Bottom + e.Bounds.Top) / 2;
        return rotation == 90 ? (e, cy, cx) : (e, cx, pageH - cy);
    }

    private async Task ClickLine(Host h, string startsWith, int rotation = 0, int occurrence = 0)
    {
        var (_, x, y) = await FindLine(h, startsWith, rotation, occurrence);
        Click(h, AtPt(h, x, y));
    }

    private static string Squash(string t) => string.Concat(t.Where(c => !char.IsWhiteSpace(c)));

    private Task<string> TextOf(string pdf, string? pw = null) => Thread.InvokeAsync(() =>
    {
        using var d = Pdf.Native.PdfDocument.Open(Thread, pdf, pw);
        using var p = d.LoadPage(0);
        using var tp = p.LoadText();
        return tp.GetText(0, tp.CharCount);
    });

    private Task<byte[]> Render(string pdf, int w, int h, string? pw = null) => Thread.InvokeAsync(() =>
    {
        using var d = Pdf.Native.PdfDocument.Open(Thread, pdf, pw);
        using var p = d.LoadPage(0);
        return p.RenderTile(1.0, 0, 0, w, h, false);
    });

    /// <summary>두 그림이 다른 픽셀 수. skip(쪽 공간, 아래가 원점) 안은 세지 않는다. 회전 쪽은 rot90으로 보기 좌표를 쪽 좌표로 바꿔 비교한다.</summary>
    private static int Diff(byte[] a, byte[] b, int w, int h, PdfRectF? skip, float margin, bool rot90 = false)
    {
        int diff = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (skip is { } r)
                {
                    // 보기 좌표(x, y 위에서) → 쪽 좌표. rot90(시계 방향 90°)이면 보기 (xv, yv) = (쪽 y, 쪽 x)
                    double px = rot90 ? y : x, py = rot90 ? x : (h - y);
                    if (px >= r.Left - margin && px <= r.Right + margin && py >= r.Bottom - margin && py <= r.Top + margin) continue;
                }
                int i = (y * w + x) * 4;
                if (Math.Abs(a[i] - b[i]) > 8 || Math.Abs(a[i + 1] - b[i + 1]) > 8 || Math.Abs(a[i + 2] - b[i + 2]) > 8) diff++;
            }
        return diff;
    }

    private async Task Save(Host h, string path) =>
        await new SaveService([], Services.Appearance, Services.Patcher, h.Session.Assets).SaveAsync(h.Session, path);

    private string Out(string name) => Path.Combine(Dir, name);

    private static List<string> BaseFonts(string pdf)
    {
        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        return doc.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Where(d => d.Elements.GetName("/Type") == "/Font" && d.Elements.GetName("/BaseFont") is { Length: > 0 })
            .Select(d => d.Elements.GetName("/BaseFont")).ToList();
    }

    private static string AllContent(string pdf)
    {
        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        var loaded = PdfEditor.ContentStream.PageContent.Load(doc.Pages[0]);
        return System.Text.Encoding.Latin1.GetString(loaded.Data ?? []);
    }

    // 문단 편집 도우미(ParagraphEditInputTests와 같은 방식)
    private static bool ParaOpen(Host h) => Ui(() => h.View.IsParagraphEditorOpen);
    private static void WaitPreview(Host h) => PumpUi(() => Ui(() => h.View.ParagraphPreviewIdleForTest && h.View.ParagraphPreviewForTest.LineCount > 0));
    private static ParagraphPreview Preview(Host h) => Ui(() => h.View.ParagraphPreviewForTest);
    private static ParagraphEditorPopup Editor(Host h) => Ui(() => h.View.ParagraphEditorForTest!);
    private static string EditorText(Host h) => Ui(() => Editor(h).Text);
    private static void SetText(Host h, string t) => HeadlessUi.Run(() => { Editor(h).SetText(t); return 0; });
    private static IReadOnlyList<ReplaceParagraphEdit> ParaEdits(Host h) => Edits(h).OfType<ReplaceParagraphEdit>().ToList();

    private static void Press(Host h, Key k, KeyModifiers m = KeyModifiers.None) => HeadlessUi.Run(() =>
    {
        var box = Editor(h).FindControl<TextBox>("Box")!;
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = k, KeyModifiers = m });
        return 0;
    });

    private static void SetPreedit(Host h, string? text) => HeadlessUi.Run(() =>
    {
        Editor(h).FindControl<TextBox>("Box")!.GetVisualDescendants().OfType<TextPresenter>().First().PreeditText = text;
        return 0;
    });

    /// <summary>한 줄 편집 창에서 확정한 편집이 n개가 되기를 기다린다. 안 되면 알림·토스트를 실패 메시지에 담는다.</summary>
    private static void WaitEdits(Host h, List<string> notices)
    {
        try { PumpUi(() => Edits(h).Count >= 1, 4000); }
        catch (Exception) { throw new Xunit.Sdk.XunitException($"편집이 생기지 않음(현재 {Edits(h).Count}). 알림 [{string.Join(" | ", notices)}] 토스트 [{Toast(h)}] 편집 창 열림 {EditorOpen(h)}"); }
    }

    private async Task OpenPara(Host h, string startsWith, int rotation = 0, int occurrence = 0)
    {
        SetTool(h, ToolKind.EditText);
        await ClickLine(h, startsWith, rotation, occurrence);
        PumpUi(() => ParaOpen(h));
        WaitPreview(h);
    }

    private async Task Commit(Host h, int expectedEditCount = 1)
    {
        Press(h, Key.Enter, KeyModifiers.Control);
        PumpUi(() => ParaEdits(h).Count == expectedEditCount);
        PumpUi(() => !ParaOpen(h));
        await Task.Delay(50);
        PumpUi(() => true);
    }

    private const string Para1Start = "도면 검토 절차는";   // 01·05 샘플의 첫 문단 첫 줄

    // ---------------------------------------------------------------- 1번 IME

    [Fact]
    public async Task S1_Ime_ComposingEnterEscIgnored_ThenEscCancelsAndEnterIsNewline()
    {
        if (await OpenUser("01-ime") is not { } h) return;
        await OpenPara(h, Para1Start);
        Assert.True(Ui(() => h.View.ParagraphSessionForTest!.Model.Lines.Length) >= 3);
        SetText(h, EditorText(h) + "가나");
        SetPreedit(h, "다");                                 // "다"를 치는 중(밑줄 글자)
        Press(h, Key.Enter, KeyModifiers.Control);            // 조합 중 확정 키: 무시
        Press(h, Key.Escape);                                 // 조합 중 Esc: 무시(창이 닫히면 안 됨)
        Press(h, Key.ImeProcessed);                           // 조합기가 먼저 삼킨 키도 무시
        Assert.True(ParaOpen(h));
        Assert.Empty(Edits(h));
        SetPreedit(h, "라");                                  // 이어서 "라마바"를 치는 중
        Press(h, Key.Escape);
        Assert.True(ParaOpen(h));
        Assert.Empty(Edits(h));
        SetPreedit(h, null);                                  // 조합이 끝났다
        HeadlessUi.Run(() => { Editor(h).ImeGuard.ForgetRecentEnd(); return 0; });   // 시간이 충분히 흘렀다
        Press(h, Key.Enter);                                 // 그냥 Enter = 줄바꿈 입력(확정 아님)
        Assert.True(ParaOpen(h));
        Assert.Empty(Edits(h));
        Press(h, Key.Escape);                                 // 조합이 끝난 뒤 Esc = 취소
        PumpUi(() => !ParaOpen(h));
        Assert.Empty(Edits(h));
        Assert.False(h.Session.IsDirty);
    }

    // ---------------------------------------------------------------- 2번 표

    [Fact]
    public async Task S2_Table_RuledCell_SingleLineEditorWithReason_AndPlainParagraphStaysMultiLine()
    {
        if (await OpenUser("02-table", width: 1500) is not { } h) return;
        var notices = new List<string>();
        h.Tab.NoticeRequested += notices.Add;
        SetTool(h, ToolKind.EditText);
        await ClickLine(h, "배관 배치도");
        WaitEditorOpen(h);
        Assert.False(ParaOpen(h));
        PumpUi(() => Toast(h) is not null);
        Assert.Equal(Strings.Para_ReasonTable, Toast(h));
        // 한 줄 창에서 글자를 붙여 확정 — 한 줄 편집이 정상 동작
        HeadlessUi.Run(() => { h.View.LineEditorForTest!.SetText("배관 배치도 개정"); return 0; });
        EditorKey(h, Key.Enter);
        WaitEdits(h, notices);
        Assert.Empty(ParaEdits(h));
    }

    [Fact]
    public async Task S2_Table_NoRulesCell_SingleLineEditor()
    {
        if (await OpenUser("02-table", width: 1500) is not { } h) return;
        SetTool(h, ToolKind.EditText);
        await ClickLine(h, "해수 펌프");
        WaitEditorOpen(h);
        Assert.False(ParaOpen(h));
        PumpUi(() => Toast(h) is not null);
        Assert.Equal(Strings.Para_ReasonTable, Toast(h));
    }

    [Fact]
    public async Task S2_Table_PlainParagraph_MultiLine_AndOneLineOnlyToggle()
    {
        if (await OpenUser("02-table", width: 1500) is not { } h) return;
        await OpenPara(h, Para1Start);
        Assert.True(Ui(() => h.View.ParagraphSessionForTest!.Model.Lines.Length) >= 3);
        HeadlessUi.Run(() => { Editor(h).FindControl<TextBox>("Box")!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape }); return 0; });
        PumpUi(() => !ParaOpen(h));
        // "한 줄만" 켜기 → 같은 문단을 눌러도 한 줄 창, 안내 없음
        HeadlessUi.Run(() => { h.Tab.OneLineOnly = true; return 0; });
        await ClickLine(h, Para1Start);
        WaitEditorOpen(h);
        Assert.False(ParaOpen(h));
        Assert.Null(Toast(h));
    }

    // ---------------------------------------------------------------- 3번 틀린 묶음

    [Fact]
    public async Task S3_Merged_TwoParagraphsBoxed_ExcludeTopButtonSplitsThem_AndEditStaysInside()
    {
        if (await OpenUser("03-merged") is not { } h) return;
        await OpenPara(h, "밸브 작동기는");
        var ps = Ui(() => h.View.ParagraphSessionForTest)!;
        int total = ps.Model.Lines.Length;
        log.WriteLine($"03-merged 위쪽 묶음 줄 수 {total}");
        Assert.Equal(6, total);   // 안내서: 두 문단 전부(일부러 틀리게 묶임)
        int own = ps.Model.Lines.Select((l, i) => (l, i)).First(t => t.l.Text.StartsWith("밸브")).i;   // 첫 문단 줄 수
        Assert.True(own is > 0 and < 6);
        string others = ps.InitialText[..ps.InitialText.IndexOf("밸브", StringComparison.Ordinal)];
        for (int i = 0; i < own; i++)
        {
            int before = Ui(() => h.View.ParagraphSessionForTest!.Model.Lines.Length);
            HeadlessUi.Run(() => { Editor(h).RequestExtend(-1, 0); return 0; });   // 위 줄 제외
            PumpUi(() => Ui(() => h.View.ParagraphSessionForTest!.Model.Lines.Length) == before - 1);
        }
        WaitPreview(h);
        Assert.StartsWith("밸브", EditorText(h));
        Assert.Contains(string.Format(Strings.Para_Lines, 6 - own, "").Split('/')[0], Ui(() => Editor(h).StatusText));   // 창 아래 "줄 N/…"가 같이 바뀜
        // 아래 줄 포함/제외도 동작(포함 시도: 아래에 이어진 줄이 없으면 그대로여도 정상, 있으면 +1)
        int n = Ui(() => h.View.ParagraphSessionForTest!.Model.Lines.Length);
        HeadlessUi.Run(() => { Editor(h).RequestExtend(0, -1); return 0; });
        PumpUi(() => Ui(() => h.View.ParagraphSessionForTest!.Model.Lines.Length) == n - 1);
        HeadlessUi.Run(() => { Editor(h).RequestExtend(0, 1); return 0; });
        PumpUi(() => Ui(() => h.View.ParagraphSessionForTest!.Model.Lines.Length) == n);
        // 낱말 하나를 고쳐 확정 → 첫 문단은 그대로
        SetText(h, EditorText(h).Replace("시험하고", "시험하며"));
        await Commit(h);
        string saved = Out("s3.pdf");
        await Save(h, saved);
        string text = Squash(await TextOf(saved));
        Assert.Contains(Squash(others[..Math.Min(others.Length, 20)]), text);   // 옆 문단 글은 그대로 있다
        Assert.Contains("시험하며", text);
        Assert.DoesNotContain("시험하고", text);
    }

    [Fact]
    public async Task S3_Merged_SignatureLineBelow_RecordsWhetherItIsBoxed_AndExcludeBottomWorks()
    {
        if (await OpenUser("03-merged") is not { } h) return;
        await OpenPara(h, "예비품은");
        var ps = Ui(() => h.View.ParagraphSessionForTest)!;
        bool sigIn = ps.Model.Lines.Any(l => l.Text.Contains("박재민"));
        log.WriteLine($"03-merged 서명 줄이 상자에 들어감: {sigIn} (줄 수 {ps.Model.Lines.Length})");
        if (sigIn)
        {
            int n = ps.Model.Lines.Length;
            HeadlessUi.Run(() => { Editor(h).RequestExtend(0, -1); return 0; });
            PumpUi(() => Ui(() => h.View.ParagraphSessionForTest!.Model.Lines.Length) == n - 1);
            Assert.DoesNotContain("박재민", EditorText(h));
        }
        else
        {
            // 서명 줄이 상자에 안 들어갔으면 "아래 줄 포함"으로 넣을 수 있고, 다시 뺄 수 있다
            int n = ps.Model.Lines.Length;
            HeadlessUi.Run(() => { Editor(h).RequestExtend(0, 1); return 0; });
            PumpUi(() => Ui(() => h.View.ParagraphSessionForTest!.Model.Lines.Length) == n + 1);
            Assert.Contains("박재민", EditorText(h));
            HeadlessUi.Run(() => { Editor(h).RequestExtend(0, -1); return 0; });
            PumpUi(() => Ui(() => h.View.ParagraphSessionForTest!.Model.Lines.Length) == n);
        }
    }

    // ---------------------------------------------------------------- 4번 원래 글꼴에 없는 글자

    private const string KoreanAdd = " 한글 글자를 넣어 봅니다";

    [Fact]
    public async Task S4_Subset_KoreanInArialParagraph_FallbackNotice_SavedTextAndFont_NoTofu()
    {
        if (await OpenUser("04-subset") is not { } h) return;
        await OpenPara(h, "The review procedure");
        SetText(h, EditorText(h) + KoreanAdd);
        PumpUi(() => Ui(() => h.View.ParagraphPreviewIdleForTest) && Preview(h).UsedAppFont, 6000);
        Assert.Contains(Strings.Para_AppFont, Ui(() => Editor(h).StatusText));   // 확정 전 예고
        await Commit(h);
        PumpUi(() => Toast(h) is not null);
        Assert.StartsWith(Strings.Msg_TextFallbackApp, Toast(h));
        string saved = Out("s4a.pdf");
        await Save(h, saved);
        Assert.Contains(Squash("한글 글자를 넣어 봅니다"), Squash(await TextOf(saved)));   // 글자 추출됨(.notdef 네모가 아님)
        Assert.Contains(BaseFonts(saved), f => f.Contains("NotoSansKR"));
        // 새 줄이 실제로 그려졌다: 편집한 줄 둘레에 잉크가 있다(빈칸/네모로 안 나옴 — 글리프 유무는 서브셋 글꼴이 보증하고 계획의 MissingGlyphs가 막는다)
        var ink = await Render(saved, 595, 842);
        var orig = await Render(Path.Combine(SampleDir!, "04-subset.pdf"), 595, 842);
        Assert.True(Diff(ink, orig, 595, 842, null, 0) > 100);
    }

    [Fact]
    public async Task S4_Subset_KoreanInGeorgiaParagraph_UsesSerifSubstitute()
    {
        if (await OpenUser("04-subset") is not { } h) return;
        await OpenPara(h, "Spare parts are listed");
        SetText(h, EditorText(h) + KoreanAdd);
        PumpUi(() => Ui(() => h.View.ParagraphPreviewIdleForTest) && Preview(h).UsedAppFont, 6000);
        await Commit(h);
        PumpUi(() => Toast(h) is not null);
        string saved = Out("s4b.pdf");
        await Save(h, saved);
        Assert.Contains(Squash("한글 글자를 넣어 봅니다"), Squash(await TextOf(saved)));
        var fonts = BaseFonts(saved);
        log.WriteLine("글꼴: " + string.Join(", ", fonts));
        Assert.Contains(fonts, f => f.Contains("NotoSerifKR"));   // 명조 계열 원본 → 명조 대체
    }

    // ---------------------------------------------------------------- 5번 두 번 고치기

    [Fact]
    public async Task S5_Twice_NoStacking_SingleUndoRestoresPreviousEdit()
    {
        if (await OpenUser("05-twice") is not { } h) return;
        var (_, px, py) = await FindLine(h, Para1Start);   // 고친 뒤에는 글 객체가 바뀌므로 처음 위치를 기억해 다시 누른다
        await OpenPara(h, Para1Start);
        string orig = EditorText(h);
        SetText(h, orig.Replace("도면", "설계도면"));
        await Commit(h);
        // 두 번째: 고친 글이 열리고, 끝에 추가
        SetTool(h, ToolKind.EditText);
        Click(h, AtPt(h, px, py));
        PumpUi(() => ParaOpen(h));
        WaitPreview(h);
        Assert.Contains("설계도면", EditorText(h));
        string second = EditorText(h) + " 추가합니다";
        SetText(h, second);
        await Commit(h);
        Assert.Single(ParaEdits(h));
        // 세 번째: 낱말 하나를 또 바꿈
        Click(h, AtPt(h, px, py));
        PumpUi(() => ParaOpen(h));
        WaitPreview(h);
        SetText(h, EditorText(h).Replace("개정 이력", "개정 기록"));
        await Commit(h);
        Assert.Single(ParaEdits(h));   // 쌓이지 않고 대체
        string saved = Out("s5.pdf");
        await Save(h, saved);
        string text = Squash(await TextOf(saved));
        // 겹쳐 쌓였다면 옛 글이 숨겨져 남아 추출에 두 번 나온다
        Assert.Equal(1, CountOf(text, Squash("관련 부서의 의견을")));
        Assert.Contains("추가합니다", text);
        Assert.Contains("개정기록", text);
        Assert.DoesNotContain("개정이력", text);
        Assert.DoesNotContain("3 Tr", AllContent(saved));
        // Ctrl+Z 한 번 = 마지막 편집만 되돌아감(직전 편집 상태)
        await h.Session.Commands.UndoAsync();
        var left = ParaEdits(h);
        Assert.Single(left);
        Assert.Contains("추가합니다", left[0].Meta.NewText);
        Assert.Contains("개정 이력", left[0].Meta.NewText);
        // 한 번 더 = 첫 편집
        await h.Session.Commands.UndoAsync();
        Assert.Single(ParaEdits(h));
        Assert.DoesNotContain("추가합니다", ParaEdits(h)[0].Meta.NewText);
        await h.Session.Commands.UndoAsync();
        Assert.Empty(ParaEdits(h));
    }

    private static int CountOf(string hay, string needle)
    {
        int n = 0, i = 0;
        while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    // ---------------------------------------------------------------- 6번 회전·배율·암호

    [Fact]
    public async Task S6a_Rotated90_EditStaysInPlace_ZeroPixelOutside()
    {
        if (await OpenUser("06a-rotated90", width: 1300) is not { } h) return;
        await OpenPara(h, Para1Start, rotation: 90);
        var ps = Ui(() => h.View.ParagraphSessionForTest)!;
        Assert.Equal(0, ps.Angle);   // 글은 쪽 좌표에서 똑바로 서 있다(쪽이 돌아 있을 뿐)
        Assert.True(Ui(() => Editor(h).EditorFontSize) > 6);
        // 상자와 편집 창이 같은 자리: 편집 창 폭(DIP) ≈ 상자 폭 × 배율 (+6 여유)
        double scale = LayoutScale(h);
        // 쪽이 돌아 있으면 상자가 화면에서 좁고 길쭉하다: 편집 창은 그 자리에 놓이되 읽을 수 있는 폭(>= 240 DIP)을 가져야 한다
        log.WriteLine($"06a 편집 창 폭 {Ui(() => Editor(h).BoxWidth):0} DIP (상자 폭 {ps.WidthPt * scale:0}, 보기 쪽 상자 {ps.Model.Box})");
        Assert.True(Ui(() => Editor(h).BoxWidth) >= 240);
        var box = ps.Model.Box;
        SetText(h, EditorText(h).Replace("승인", "결재"));
        await Commit(h);
        string saved = Out("s6a.pdf");
        await Save(h, saved);
        Assert.Contains("결재", await TextOf(saved));
        // 쪽 회전 그대로 저장됨 + 상자 밖 0 px
        using (var doc = PdfReader.Open(saved, PdfDocumentOpenMode.Import)) Assert.Equal(90, doc.Pages[0].Rotate);
        string src = Path.Combine(SampleDir!, "06a-rotated90.pdf");
        var a = await Render(src, 842, 595);
        var b = await Render(saved, 842, 595);
        Assert.Equal(0, Diff(a, b, 842, 595, box, 8, rot90: true));
    }

    private static double LayoutScale(Host h) => Ui(() =>
    {
        var f = typeof(DocumentView).GetField("_layout", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((PageLayout)f.GetValue(h.View)!).Scale;
    });

    private static Avalonia.Rect CurrentPageRect(Host h) => Ui(() =>
    {
        var f = typeof(DocumentView).GetField("_layout", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((PageLayout)f.GetValue(h.View)!).GetPageRect(0);
    });

    /// <summary>화면 배율(우리 쪽 줌)을 1.25배·1.5배로 흉내 — 편집 창/상자 좌표·글자 크기가 배율을 따라가는지(실제 Windows 125/150 % DPI 배율은 못 흉내 냄: 사용자 점검).</summary>
    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public async Task S6b_Zoom_BoxAndEditorFollowScale_ClickStillWorks(double factor)
    {
        if (await OpenUser("06b-zoom") is not { } h) return;
        HeadlessUi.Run(() => { h.Tab.Scale = h.Tab.Scale * factor; return 0; });
        PumpUi(() => Math.Abs(LayoutScale(h) - K * factor) < 1e-6);
        var (e, x, y) = await FindLine(h, Para1Start);
        var pr = CurrentPageRect(h);
        double s = LayoutScale(h);
        SetTool(h, ToolKind.EditText);
        Click(h, new Avalonia.Point(pr.X + x * s, pr.Y + y * s));
        PumpUi(() => ParaOpen(h));
        WaitPreview(h);
        var ps = Ui(() => h.View.ParagraphSessionForTest)!;
        Assert.Equal(ps.SizePt * s, Ui(() => Editor(h).EditorFontSize), 0.5);           // 글자 크기가 배율을 따름(잘리지 않음)
        Assert.Equal(ps.WidthPt * s + 6, Ui(() => Editor(h).BoxWidth), 3);                // 폭이 상자와 같음
        Assert.True(Ui(() => Editor(h).CommitEnabled));
        // 더 키우면 따라간다
        HeadlessUi.Run(() => { h.Tab.Scale = h.Tab.Scale * 1.2; return 0; });
        PumpUi(() => Math.Abs(Ui(() => Editor(h).EditorFontSize) - ps.SizePt * LayoutScale(h)) < 0.5);
        SetText(h, EditorText(h) + " 확인");
        await Commit(h);
    }

    [Fact]
    public async Task S6c_Encrypted_ParagraphOff_SingleLineStillEdits()
    {
        if (await OpenUser("06c-encrypted", Pw) is not { } h) return;
        var notices = new List<string>();
        h.Tab.NoticeRequested += notices.Add;
        SetTool(h, ToolKind.EditText);
        await ClickLine(h, Para1Start);
        WaitEditorOpen(h);
        Assert.False(ParaOpen(h));
        PumpUi(() => Toast(h) is not null);
        Assert.Equal(Strings.Para_Encrypted, Toast(h));
        string t = Ui(() => h.View.LineEditorForTest!.Text);
        HeadlessUi.Run(() => { h.View.LineEditorForTest!.SetText(t[..^1]); return 0; });   // 글꼴에 있는 글자만 쓰는 고침(끝 글자 지우기)
        EditorKey(h, Key.Enter);
        WaitEdits(h, notices);
        Assert.Empty(ParaEdits(h));
    }

    /// <summary>안내서 6-다는 "한두 글자 고치고 확정"이라고 했다. 서브셋 글꼴에 없는 글자(여기서는 '!')를 넣으면 암호 문서는 앱 글꼴로 바꿀 수 없어 어떻게 되는지 기록한다.</summary>
    [Fact]
    public async Task S6c_Encrypted_CharNotInSubset_RecordsBehavior()
    {
        if (await OpenUser("06c-encrypted", Pw) is not { } h) return;
        var notices = new List<string>();
        h.Tab.NoticeRequested += notices.Add;
        SetTool(h, ToolKind.EditText);
        await ClickLine(h, Para1Start);
        WaitEditorOpen(h);
        string t = Ui(() => h.View.LineEditorForTest!.Text);
        HeadlessUi.Run(() => { h.View.LineEditorForTest!.SetText(t + "!"); return 0; });
        EditorKey(h, Key.Enter);
        await Task.Delay(800);
        PumpUi(() => true);
        log.WriteLine($"암호 문서 + 서브셋에 없는 글자: 편집 {Edits(h).Count}개, 편집 창 열림 {EditorOpen(h)}, 알림 [{string.Join(" | ", notices)}], 토스트 {Toast(h)}");
        Assert.True(Edits(h).Count == 1 || notices.Count > 0 || EditorOpen(h) || Toast(h) is { Length: > 0 },
            "결함: 아무 반응이 없다(편집도 안 되고 알림도 없음)");
    }


    // ---------------------------------------------------------------- 사용자가 스크린샷으로 알려 온 3건 (현재 상태 고정 — 고치면 이 시험의 기대를 뒤집는다)

    /// <summary>① 글꼴이 바뀌면(원래 글꼴에 없는 글자 하나만 넣어도) 다시 쓰는 줄이 대체 글꼴로 바뀌어 줄 끊는 자리가 원본과 많이 달라진다.</summary>
    [Fact]
    public async Task KnownIssue1_FontSubstitution_ReflowsMostOfTheParagraph()
    {
        if (await OpenUser("01-ime") is not { } h) return;
        SetTool(h, ToolKind.EditText);
        var page = h.Session.Pages[0].Id;
        var (pick, _, _) = await FindLine(h, Para1Start);
        var idx = await Thread.InvokeAsync(() => h.Session.GetObjectIndex(page));
        var start = await ParagraphController.StartAsync(h.Tab, page, pick, idx);
        var ps = Assert.IsType<ParagraphSession>(start.Session);
        var orig = ps.Model.Lines.Select(l => l.Text.Length).ToList();
        string t0 = ps.InitialText;
        int at = t0.IndexOf('도');
        // 같은 글꼴 안의 글자 하나 교체(기준) vs 서브셋에 없는 글자 하나 교체(대체 글꼴로 넘어감)
        (int Diff, int Rewritten, bool App, List<int> Lens) Run(char c)
        {
            string text = t0[..at] + c + t0[(at + 1)..];
            var plan = Thread.InvokeAsync(() => ParagraphEditPlanner.Plan(h.Session, page, ps.Model, text, Services.Font, null, ps.Korean, null, Services.Catalog)).GetAwaiter().GetResult();
            Assert.True(plan.Ok);
            var now = plan.Layout!.Lines.Select(l => l.Text.Length).ToList();
            int diff = Enumerable.Range(0, Math.Min(now.Count, orig.Count)).Count(i => now[i] != orig[i]) + Math.Abs(now.Count - orig.Count);
            return (diff, plan.Layout.LastRewritten - plan.Layout.FirstRewritten + 1, plan.UsedAppFont, now);
        }
        var same = Run('면');
        var sub = Run('뷁');
        log.WriteLine($"원본 줄 길이 [{string.Join(",", orig)}] | 같은 글꼴 글자: [{string.Join(",", same.Lens)}] 끊는 자리 다른 줄 {same.Diff}, 다시 쓴 줄 {same.Rewritten} | 서브셋에 없는 글자: [{string.Join(",", sub.Lens)}] 다른 줄 {sub.Diff}, 다시 쓴 줄 {sub.Rewritten}");
        Assert.False(same.App);
        Assert.True(sub.App);
        // 현재 동작(문제 ①): 기준은 끊는 자리가 그대로(0), 대체 글꼴은 문단 전체가 다시 쓰이고 모든 줄의 끊는 자리가 달라진다
        Assert.Equal(0, same.Diff);
        Assert.Equal(1, same.Rewritten);
        Assert.Equal(orig.Count, sub.Rewritten);
        Assert.Equal(orig.Count, sub.Diff);
    }

    /// <summary>② 편집 창 안의 줄바꿈(Avalonia 기본 글꼴·UAX#14: 한글은 음절 사이에서도 끊김)이 PDF 계획(공백에서 끊기)과 다르다. 헤드리스에서는 글꼴 폭이 달라 실제 화면의 줄 수 차이는 못 재고, 원인이 되는 구조를 고정한다.</summary>
    [Fact]
    public async Task KnownIssue2_EditorWrapDiffersFromPlan()
    {
        if (await OpenUser("01-ime") is not { } h) return;
        await OpenPara(h, Para1Start);
        var box = Ui(() => Editor(h).FindControl<TextBox>("Box")!);
        var (visualLines, midWordBreaks) = Ui(() =>
        {
            Editor(h).SetWidth(180);   // 좁혀서 한글 낱말이 줄 끝에 걸리게 한다
            Editor(h).UpdateLayout();
            var pres = box.GetVisualDescendants().OfType<TextPresenter>().First();
            int mid = 0;
            foreach (var l in pres.TextLayout.TextLines)
            {
                int end = l.FirstTextSourceIndex + l.Length;
                if (end < box.Text!.Length && end > 0 && !char.IsWhiteSpace(box.Text[end - 1]) && !char.IsWhiteSpace(box.Text[end])) mid++;
            }
            return (pres.TextLayout.TextLines.Count, mid);
        });
        log.WriteLine($"편집 창(폭 180) 줄 수 {visualLines}, 낱말 중간 끊김 {midWordBreaks}곳 | 계획은 항상 공백에서 끊음(KoreanLineBreak.{Ui(() => h.View.ParagraphSessionForTest!.Korean)}) | 글꼴 {Ui(() => box.FontFamily)}");
        Assert.True(midWordBreaks > 0, "편집 창이 한글 낱말 중간에서 끊지 않게 되면 문제 ②가 고쳐진 것 — 기대를 뒤집는다");
        Assert.Equal(KoreanLineBreak.Word, Ui(() => h.View.ParagraphSessionForTest!.Korean));
        // 구조적 원인: 편집 창은 PDF 글꼴이 아니라 기본 글꼴을 쓴다
        Assert.DoesNotContain("Malgun", Ui(() => box.FontFamily.ToString()));
        Assert.False(Ui(() => box.IsSet(TemplatedControl.FontFamilyProperty)));
    }

    /// <summary>③ 편집 창 아래 안내 글씨가 작고 폭이 좁아 여러 줄로 접힌다.</summary>
    [Fact]
    public async Task KnownIssue3_StatusText_SmallAndFolded()
    {
        if (await OpenUser("04-subset") is not { } h) return;
        await OpenPara(h, "The review procedure");
        SetText(h, EditorText(h) + KoreanAdd);   // 앱 글꼴 예고 + 글꼴 섞임 안내
        PumpUi(() => Ui(() => h.View.ParagraphPreviewIdleForTest) && Preview(h).UsedAppFont, 6000);
        var (size, boxSize, width, height, maxWidth, text) = Ui(() =>
        {
            var st = Editor(h).FindControl<TextBlock>("Status")!;
            Editor(h).UpdateLayout();
            return (st.FontSize, Editor(h).EditorFontSize, st.Bounds.Width, st.Bounds.Height, st.MaxWidth, st.Text ?? "");
        });
        double lineH = size * 1.35;
        log.WriteLine($"안내 글씨 {size}pt(편집 글 {boxSize:0.0}) 폭 {width:0}/{maxWidth:0} 높이 {height:0.0} ≈ {height / lineH:0.0}줄 | {text}");
        Assert.True(size <= 11);                       // 작다
        Assert.True(maxWidth <= 360);                  // 좁다
        Assert.True(height > lineH * 1.5, "여러 줄로 접힌다");
    }
}
