using System.Diagnostics;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Panels;
using PdfEditor.App.Resources;
using PdfEditor.Document;
using PdfEditor.Document.Annotations;
using PdfEditor.TestFixtures;

namespace PdfEditor.App.Tests.Panels;

/// <summary>코멘트 목록 모델 (M3b Task 2): 모든 주석, 답글 들여쓰기, 읽기 전용 원본, 거르기·정렬, 일부 쪽만 갱신, 이동.</summary>
public class CommentsViewModelTests : PanelTestBase
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private string P(string name) => Path.Combine(Dir, name);

    private async Task<DocumentTabViewModel> OpenFixture(string name, Action<string> create, Action<Action>? post = null)
    {
        create(P(name));
        var tab = await OpenTab(P(name), null, post);
        await tab.Session!.AnnotationsReady;
        return tab;
    }

    private async Task<DocumentTabViewModel> OpenLabeled(int pages)
    {
        string p = P(Guid.NewGuid().ToString("N") + ".pdf");
        TestPdfFactory.CreateLabeled(p, [.. Enumerable.Repeat((400, 600), pages)]);
        var tab = await OpenTab(p);
        await tab.Session!.AnnotationsReady;
        return tab;
    }

    private static Annotation Note(DocumentTabViewModel tab, string? author, string? contents, DateTimeOffset? when = null,
        AnnotKind kind = AnnotKind.Square, float x = 10, float y = 10) => new()
    {
        Id = tab.Session!.Annotations.NewId(), Kind = kind, Rect = new PdfRectF(x, y, x + 40, y + 30), Color = new PdfColor(200, 0, 0),
        Author = author, Contents = contents, CreatedAt = when ?? T0, ModifiedAt = when ?? T0, Flags = PdfAnnotFlags.Print,
    };

    private static Task Add(DocumentTabViewModel tab, PageId page, params Annotation[] items)
        => tab.RunAnnotationCommandAsync(new ChangeAnnotationsCommand(page, [.. items.Select(a => new AnnotationChange(null, a))]));

    [Fact]
    public async Task ListsAllKindsWithRepliesIndented()
    {
        var tab = await OpenFixture("th.pdf", AnnotatedPdfFactory.CreateThreads);
        using var vm = new CommentsViewModel(tab);

        // note + 답글 2개(답글에 단 답글도 부모 아래 한 단계) + 읽기 전용 3개(Link, 부모가 Link인 답글, 지원하지 않는 상태 모델). Popup·상태 주석은 줄이 아니다.
        Assert.Equal(6, vm.TotalCount);
        Assert.Equal(6, vm.ShownCount);
        int n = vm.Items.ToList().FindIndex(i => i.Editable && i.Depth == 0);
        Assert.True(n >= 0);
        var note = vm.Items[n];
        Assert.Equal(Strings.AnnotKind_Text, note.KindName);
        Assert.Equal(ReviewState.Rejected, note.State);   // 가장 최근 상태
        Assert.Equal(AnnotatedPdfFactory.ReplyText, vm.Items[n + 1].Contents);
        Assert.Equal(1, vm.Items[n + 1].Depth);
        Assert.Equal(Strings.AnnotKind_Reply, vm.Items[n + 1].KindName);
        Assert.Equal("답변자", vm.Items[n + 1].Author);
        Assert.Equal("답답글", vm.Items[n + 2].Contents);
        Assert.Equal(1, vm.Items[n + 2].Depth);
        Assert.True(vm.Items[n + 1].Editable);
        Assert.Equal(1, vm.Items.Count(i => i.Depth == 0 && i.Editable));
        Assert.DoesNotContain(vm.Items, i => i.Contents is "상태 Accepted" or "상태 Rejected");   // 지원하는 상태 주석은 줄이 아니다 (Marked 모델은 읽기 전용 줄)
        Assert.DoesNotContain(vm.Items, i => i.ReadOnlyIndex == 1);   // Popup
    }

    [Fact]
    public async Task ReplaceGroupIsOneRow()
    {
        var tab = await OpenFixture("rg.pdf", AnnotatedPdfFactory.CreateReplaceGroup);
        using var vm = new CommentsViewModel(tab);

        // [0] 취소선 + [1] 삽입 기호 = 한 줄, [2] 메모, [3] 읽기 전용 답글
        Assert.Equal(3, vm.Items.Count);
        var row = Assert.Single(vm.Items, i => i.KindName == Strings.AnnotKind_ReplaceText);
        Assert.True(row.Editable);
        Assert.Equal("new text", row.Contents);
        var page = tab.Session!.Pages[0].Id;
        var members = tab.Session.Annotations.GroupOf(page, row.Id!.Value);
        Assert.Equal(AnnotKind.StrikeOut, members[0].Kind);
        Assert.Equal(members[0].Id, row.Id);
        Assert.DoesNotContain(vm.Items, i => i.KindName == Strings.AnnotKind_Caret && i.Editable);
    }

    [Fact]
    public async Task ReadOnlyOriginals_ListedNotEditable()
    {
        var tab = await OpenFixture("mixed.pdf", AnnotatedPdfFactory.CreateMixed);
        using var vm = new CommentsViewModel(tab);

        var link = Assert.Single(vm.Items, i => i.KindName == Strings.AnnotKind_Link);
        Assert.False(link.Editable);
        Assert.Null(link.Id);
        Assert.Equal(0, link.ReadOnlyIndex);
        Assert.NotEqual(default, link.Rect);
        // [5] /RT /Group 답글: 읽기 전용으로 내용·작성 정보가 보인다
        var reply = Assert.Single(vm.Items, i => i.ReadOnlyIndex == 5);
        Assert.False(reply.Editable);
        Assert.Equal(AnnotatedPdfFactory.ReplyText, reply.Contents);
        Assert.DoesNotContain(vm.Items, i => i.ReadOnlyIndex == 4);   // Popup
        // 편집 가능한 원본도 같이 나온다 (13개 중 Popup 제외 12개)
        Assert.Equal(12, vm.TotalCount);
        Assert.Equal(10, vm.Items.Count(i => i.Editable));
        Assert.Equal(2, vm.Items.Count(i => !i.Editable));
        var memo = Assert.Single(vm.Items, i => i.Contents == AnnotatedPdfFactory.MemoText);
        Assert.True(memo.Editable);
        // 클라우드 Square는 종류 이름이 클라우드
        Assert.Contains(vm.Items, i => i.KindName == Strings.AnnotKind_Cloud);
    }

    [Fact]
    public async Task FilterByKindAuthorStateText()
    {
        var tab = await OpenLabeled(3);
        var pages = tab.Session!.Pages;
        var hl = Note(tab, "kim", "check the budget", kind: AnnotKind.Square, x: 10, y: 500);
        var sq2 = Note(tab, "lee", "fix typo", kind: AnnotKind.Circle, x: 10, y: 400);
        var note = Note(tab, "kim", "memo here", kind: AnnotKind.Text, x: 10, y: 300);
        await Add(tab, pages[0].Id, hl, sq2, note);
        var third = Note(tab, "park", "last page", kind: AnnotKind.Square, x: 10, y: 10);
        await Add(tab, pages[2].Id, third);
        var reply = ThreadAnnotations.NewReply(tab.Session.Annotations, sq2, "agreed by choi", "choi", T0.AddMinutes(1));
        await tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Add(pages[0].Id, reply));
        var state = ThreadAnnotations.NewState(tab.Session.Annotations, hl, ReviewState.Accepted, "kim", T0.AddMinutes(2), "{0}:{1}", "ok");
        await tab.RunAnnotationCommandAsync(ChangeAnnotationsCommand.Add(pages[0].Id, state));

        using var vm = new CommentsViewModel(tab);
        Assert.Equal(5, vm.TotalCount);   // 주석 4 + 답글 1 (상태 주석은 줄이 아님)
        Assert.Equal(["choi", "kim", "lee", "park"], vm.Authors);

        vm.KindFilter = new HashSet<string> { Strings.AnnotKind_Circle };
        Assert.Equal([sq2.Id, reply.Id], vm.Items.Select(i => i.Id!.Value));   // 묶음 전체(부모 + 답글)
        vm.KindFilter = new HashSet<string>();

        vm.AuthorFilter = "kim";
        Assert.Equal([hl.Id, note.Id], vm.Items.Select(i => i.Id!.Value));
        vm.AuthorFilter = "choi";   // 답글 작성자로도 찾는다 (부모 묶음이 보인다)
        Assert.Equal([sq2.Id, reply.Id], vm.Items.Select(i => i.Id!.Value));
        vm.AuthorFilter = null;

        vm.StateFilter = ReviewState.Accepted;
        Assert.Equal([hl.Id], vm.Items.Select(i => i.Id!.Value));
        vm.StateFilter = null;

        vm.SearchText = "TYPO";   // 내용, 대소문자 무시
        Assert.Equal([sq2.Id, reply.Id], vm.Items.Select(i => i.Id!.Value));
        vm.SearchText = "park";    // 작성자
        Assert.Equal([third.Id], vm.Items.Select(i => i.Id!.Value));
        vm.SearchText = "";

        vm.PageRange = "3";
        Assert.Equal([third.Id], vm.Items.Select(i => i.Id!.Value));
        Assert.True(vm.PageRangeValid);
        vm.PageRange = "9";   // 잘못된 범위: 거르지 않고 알린다
        Assert.False(vm.PageRangeValid);
        Assert.Equal(5, vm.ShownCount);
        vm.PageRange = "";
        Assert.Equal(5, vm.ShownCount);
        Assert.Equal(5, vm.TotalCount);
    }

    [Fact]
    public async Task SortByDateAuthor()
    {
        var tab = await OpenLabeled(2);
        var pages = tab.Session!.Pages;
        var a = Note(tab, "lee", "a", T0.AddDays(3), x: 10, y: 500);
        var b = Note(tab, "kim", "b", T0.AddDays(1), AnnotKind.Circle, x: 10, y: 400);
        var c = Note(tab, null, "c", null, AnnotKind.Text, x: 10, y: 300);
        var d = Note(tab, "Park", "d", T0.AddDays(2), x: 10, y: 200);
        await Add(tab, pages[0].Id, a, b);
        await Add(tab, pages[1].Id, c, d);
        using var vm = new CommentsViewModel(tab);

        List<AnnotId> Order() => [.. vm.Items.Select(i => i.Id!.Value)];

        // 쪽(기본): 쪽 번호, 위에서 아래
        Assert.Equal([a.Id, b.Id, c.Id, d.Id], Order());
        vm.SortBy = CommentSort.Date;
        Assert.Equal([c.Id, b.Id, d.Id, a.Id], Order());   // c는 시각을 안 줘서 T0
        vm.SortBy = CommentSort.Author;
        Assert.Equal([b.Id, a.Id, d.Id], Order()[..3]);   // kim, lee, Park (대소문자 무시); 작성자 없음은 맨 끝
        Assert.Equal(c.Id, Order()[3]);
        vm.SortDescending = true;
        Assert.Equal([d.Id, a.Id, b.Id], Order()[..3]);
        Assert.Equal(c.Id, Order()[3]);   // 값이 없는 것은 방향과 관계없이 끝
        vm.SortDescending = false;
        vm.SortBy = CommentSort.Kind;
        var kinds = vm.Items.Select(i => i.KindName).ToList();
        Assert.Equal(kinds.OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase).ToList(), kinds);
    }

    [Fact]
    public async Task Filter1000_Under50ms()
    {
        var tab = await OpenLabeled(4);
        var pages = tab.Session!.Pages;
        for (int p = 0; p < 4; p++)
        {
            var items = Enumerable.Range(0, 250).Select(i => Note(tab, "author" + (i % 10), $"comment number {i} on page {p}",
                T0.AddMinutes(i * 7 % 997), i % 3 == 0 ? AnnotKind.Circle : AnnotKind.Square, x: 10 + (i % 8) * 45, y: 20 + (i / 8) * 17)).ToArray();
            await Add(tab, pages[p].Id, items);
        }
        using var vm = new CommentsViewModel(tab);
        Assert.Equal(1000, vm.TotalCount);
        Assert.Equal(1000, vm.ShownCount);

        // 첫 호출(JIT)을 뺀 뒤, 연산마다 세 번 중 가장 빠른 것이 50 ms 안이어야 한다.
        vm.SearchText = "warm";
        vm.SearchText = "";
        foreach (var (name, apply, undo) in new (string, Action, Action)[]
        {
            ("search", () => vm.SearchText = "number 12", () => vm.SearchText = ""),
            ("author", () => vm.AuthorFilter = "author3", () => vm.AuthorFilter = null),
            ("kind", () => vm.KindFilter = new HashSet<string> { Strings.AnnotKind_Circle }, () => vm.KindFilter = new HashSet<string>()),
            ("sort date", () => vm.SortBy = CommentSort.Date, () => vm.SortBy = CommentSort.Page),
            ("sort author", () => vm.SortBy = CommentSort.Author, () => vm.SortBy = CommentSort.Page),
            ("range", () => vm.PageRange = "2-3", () => vm.PageRange = ""),
        })
        {
            long best = long.MaxValue;
            for (int r = 0; r < 3; r++)
            {
                var sw = Stopwatch.StartNew();
                apply();
                sw.Stop();
                best = Math.Min(best, sw.ElapsedTicks);
                undo();
            }
            double ms = best * 1000.0 / Stopwatch.Frequency;
            Assert.True(ms < 50, $"{name} took {ms:F1} ms");
        }
        Assert.Equal(1000, vm.ShownCount);
    }

    [Fact]
    public async Task StoreChanged_UpdatesOnlyThatPage()
    {
        var tab = await OpenLabeled(3);
        var pages = tab.Session!.Pages;
        await Add(tab, pages[0].Id, Note(tab, "a", "p1"));
        await Add(tab, pages[2].Id, Note(tab, "a", "p3"));
        using var vm = new CommentsViewModel(tab);
        var before = vm.Items.ToList();
        int c1 = vm.RebuildCount(pages[0].Id), c2 = vm.RebuildCount(pages[1].Id), c3 = vm.RebuildCount(pages[2].Id);

        var added = Note(tab, "b", "p2 new");
        await Add(tab, pages[1].Id, added);

        Assert.Equal(c1, vm.RebuildCount(pages[0].Id));
        Assert.Equal(c3, vm.RebuildCount(pages[2].Id));
        Assert.Equal(c2 + 1, vm.RebuildCount(pages[1].Id));
        Assert.Equal(3, vm.Items.Count);
        // 바뀌지 않은 쪽의 줄은 같은 인스턴스 그대로
        Assert.Same(before.Single(i => i.PageNumber == 1), vm.Items.Single(i => i.PageNumber == 1));
        Assert.Same(before.Single(i => i.PageNumber == 3), vm.Items.Single(i => i.PageNumber == 3));
        Assert.Contains(vm.Items, i => i.Id == added.Id && i.PageNumber == 2);
        Assert.Contains("b", vm.Authors);

        // 실행 취소도 그 쪽만
        await tab.Session.Commands.UndoAsync();
        Assert.Equal(c2 + 2, vm.RebuildCount(pages[1].Id));
        Assert.Equal(c1, vm.RebuildCount(pages[0].Id));
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public async Task StoreChanged_MarshalsToUiBeforeTouchingItems()
    {
        var queue = new Queue<Action>();
        string p = P("ui.pdf");
        TestPdfFactory.CreateLabeled(p, [(400, 600)]);
        var tab = await OpenTab(p, null, a => { lock (queue) queue.Enqueue(a); });
        await tab.Session!.AnnotationsReady;
        using var vm = new CommentsViewModel(tab);
        var page = tab.Session.Pages[0].Id;

        await Add(tab, page, Note(tab, "a", "x"));
        Assert.Empty(vm.Items);          // UI 스레드로 넘기기 전에는 목록을 건드리지 않는다
        while (true)
        {
            Action? next;
            lock (queue) next = queue.Count > 0 ? queue.Dequeue() : null;
            if (next is null) break;
            next();
        }
        Assert.Single(vm.Items);
    }

    [Fact]
    public async Task GoTo_SelectsEditable_NavigatesReadOnly()
    {
        var tab = await OpenLabeled(3);
        var pages = tab.Session!.Pages;
        var a = Note(tab, "a", "on page three", kind: AnnotKind.Square, x: 30, y: 40);
        await Add(tab, pages[2].Id, a);
        using var vm = new CommentsViewModel(tab);
        var revealed = new List<(PageId, PdfRectF)>();
        tab.RevealRequested += (pg, r) => revealed.Add((pg, r));

        tab.Tool = ToolKind.Hand;
        var item = Assert.Single(vm.Items);
        vm.GoToCommand.Execute(item);
        Assert.Equal(2, tab.CurrentPageIndex);
        Assert.Equal(ToolKind.AnnotSelect, tab.Tool);
        Assert.Equal(pages[2].Id, tab.Selection.Page);
        Assert.Equal([a.Id], tab.Selection.Ids);
        Assert.Equal([(pages[2].Id, a.Rect)], revealed);

        // 읽기 전용: 이동만 하고 선택은 그대로
        var ro = await OpenFixture("mixed2.pdf", AnnotatedPdfFactory.CreateMixed);
        using var vm2 = new CommentsViewModel(ro);
        var shown = new List<(PageId, PdfRectF)>();
        ro.RevealRequested += (pg, r) => shown.Add((pg, r));
        var link = vm2.Items.Single(i => i.KindName == Strings.AnnotKind_Link);
        var other = vm2.Items.First(i => i.Editable);
        ro.Selection.Set(other.Page, [other.Id!.Value]);
        vm2.GoTo(link);
        Assert.Equal(0, ro.CurrentPageIndex);
        Assert.Equal([other.Id.Value], ro.Selection.Ids);
        Assert.Equal([(link.Page, link.Rect)], shown);
    }

    // ---- 답글·상태·삭제 (M3b Task 3) ----

    private static IEnumerable<Annotation> AllOn(DocumentTabViewModel tab, PageId page)
        => tab.Session!.Annotations.Get(page).Items.Select(v => v.Data);

    [Fact]
    public async Task Reply_OneCommand_AppearsIndented()
    {
        var tab = await OpenLabeled(1);
        var page = tab.Session!.Pages[0].Id;
        await Add(tab, page, Note(tab, "kim", "root note"));
        using var vm = new CommentsViewModel(tab);
        var root = Assert.Single(vm.Items);
        long undoDepth = tab.Session.Commands.Version;

        await vm.ReplyAsync(root, "  first reply  ");

        Assert.Equal(undoDepth + 1, tab.Session.Commands.Version);   // 명령 하나
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(0, vm.Items[0].Depth);
        Assert.Equal(1, vm.Items[1].Depth);
        Assert.Equal("first reply", vm.Items[1].Contents);
        Assert.Equal(Strings.AnnotKind_Reply, vm.Items[1].KindName);
        Assert.Equal(DocumentTabViewModel.CurrentAuthor, vm.Items[1].Author);
        var stored = AllOn(tab, page).Single(a => a.InReplyTo is not null);
        Assert.Equal(root.Id, stored.InReplyTo);

        await tab.Session.Commands.UndoAsync();
        Assert.Single(vm.Items);
        await tab.Session.Commands.RedoAsync();
        Assert.Equal(2, vm.Items.Count);

        // 빈 글은 아무것도 하지 않는다
        await vm.ReplyAsync(root, "   ");
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public async Task Reply_ToAnnotationWithReplies_GoesToThreadEnd()
    {
        var tab = await OpenLabeled(1);
        var page = tab.Session!.Pages[0].Id;
        await Add(tab, page, Note(tab, "kim", "root note"));
        using var vm = new CommentsViewModel(tab);
        var root = vm.Items[0];

        await vm.ReplyAsync(root, "one");
        await vm.ReplyAsync(root, "two");
        await vm.ReplyAsync(vm.Items[1], "three (to the first reply)");

        Assert.Equal(["root note", "one", "two", "three (to the first reply)"], vm.Items.Select(i => i.Contents));
        Assert.Equal([0, 1, 1, 1], vm.Items.Select(i => i.Depth));
    }

    [Fact]
    public async Task SetState_ShowsInRow_UndoReverts()
    {
        var tab = await OpenLabeled(1);
        var page = tab.Session!.Pages[0].Id;
        await Add(tab, page, Note(tab, "kim", "root note"));
        using var vm = new CommentsViewModel(tab);
        var root = vm.Items[0];
        Assert.Equal(ReviewState.None, root.State);
        long undoDepth = tab.Session.Commands.Version;

        await vm.SetStateAsync(root, ReviewState.Accepted);
        Assert.Equal(undoDepth + 1, tab.Session.Commands.Version);
        Assert.Single(vm.Items);                                   // 상태 주석은 줄이 아니다
        Assert.Equal(ReviewState.Accepted, vm.Items[0].State);

        await vm.SetStateAsync(vm.Items[0], ReviewState.Rejected);
        Assert.Equal(ReviewState.Rejected, vm.Items[0].State);

        // 문구: 상태 이름 뒤에 조사를 붙이지 않는 틀
        var state = AllOn(tab, page).Where(a => a.State == ReviewState.Rejected).Single();
        Assert.Equal(string.Format(Strings.Comment_StateTemplate, DocumentTabViewModel.CurrentAuthor, Strings.State_Rejected), state.Contents);
        Assert.DoesNotContain("로", state.Contents!.Replace(Strings.Comment_StateTemplate.Replace("{0}", "").Replace("{1}", ""), ""));

        await tab.Session.Commands.UndoAsync();
        Assert.Equal(ReviewState.Accepted, vm.Items[0].State);
        await tab.Session.Commands.UndoAsync();
        Assert.Equal(ReviewState.None, vm.Items[0].State);
    }

    [Fact]
    public async Task SetState_OnReplyRow_AppliesToThreadRoot()
    {
        var tab = await OpenLabeled(1);
        var page = tab.Session!.Pages[0].Id;
        await Add(tab, page, Note(tab, "kim", "root note"));
        using var vm = new CommentsViewModel(tab);
        await vm.ReplyAsync(vm.Items[0], "reply");

        await vm.SetStateAsync(vm.Items[1], ReviewState.Completed);

        Assert.Equal(ReviewState.Completed, vm.Items[0].State);
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public async Task Delete_RemovesThread()
    {
        var tab = await OpenLabeled(1);
        var page = tab.Session!.Pages[0].Id;
        await Add(tab, page, Note(tab, "kim", "root note"), Note(tab, "lee", "other", x: 100, y: 100));
        using var vm = new CommentsViewModel(tab);
        var root = vm.Items.Single(i => i.Contents == "root note");
        await vm.ReplyAsync(root, "r1");
        await vm.SetStateAsync(vm.Items.Single(i => i.Contents == "root note"), ReviewState.Accepted);
        await vm.ReplyAsync(vm.Items.Single(i => i.Contents == "root note"), "r2");
        Assert.Equal(4, vm.Items.Count);
        Assert.Equal(5, AllOn(tab, page).Count());
        long undoDepth = tab.Session!.Commands.Version;

        await vm.DeleteAsync(vm.Items.Single(i => i.Contents == "root note"));

        Assert.Equal(undoDepth + 1, tab.Session.Commands.Version);   // 명령 하나
        Assert.Equal(["other"], vm.Items.Select(i => i.Contents));
        Assert.Single(AllOn(tab, page));                               // 고아 답글·상태가 남지 않는다

        await tab.Session.Commands.UndoAsync();
        Assert.Equal(5, AllOn(tab, page).Count());
        Assert.Equal(4, vm.Items.Count);
        Assert.Equal(ReviewState.Accepted, vm.Items.Single(i => i.Depth == 0 && i.Contents == "root note").State);
    }

    [Fact]
    public async Task Delete_ReadOnlyRow_Ignored()
    {
        var ro = await OpenFixture("mixed3.pdf", AnnotatedPdfFactory.CreateMixed);
        using var vm = new CommentsViewModel(ro);
        var link = vm.Items.Single(i => i.KindName == Strings.AnnotKind_Link);
        int n = vm.Items.Count;
        await vm.DeleteAsync(link);
        Assert.Equal(n, vm.Items.Count);
    }

    [Fact]
    public async Task Encrypted_ReplyAndStateDisabled()
    {
        string p = P("enc.pdf");
        TestPdfFactory.CreateEncrypted(p, 1, 400, 600, "secret");
        var tab = await OpenTab(p, "secret");
        await tab.Session!.AnnotationsReady;
        var page = tab.Session.Pages[0].Id;
        await Add(tab, page, Note(tab, "kim", "root note"));
        using var vm = new CommentsViewModel(tab);

        Assert.False(vm.CanReply);
        Assert.Equal(Strings.Msg_RepliesUnavailableEncrypted, vm.ReplyNotice);
        var root = Assert.Single(vm.Items);          // 보기는 된다
        long undoDepth = tab.Session.Commands.Version;

        await vm.ReplyAsync(root, "nope");
        await vm.SetStateAsync(root, ReviewState.Accepted);

        Assert.Single(vm.Items);
        Assert.Equal(undoDepth, tab.Session.Commands.Version);
        Assert.Single(AllOn(tab, page));
        Assert.False(vm.ReplyCommand.CanExecute(root));
    }

    [Fact]
    public async Task NormalDoc_CanReply_NoNotice()
    {
        var tab = await OpenLabeled(1);
        using var vm = new CommentsViewModel(tab);
        Assert.True(vm.CanReply);
        Assert.Null(vm.ReplyNotice);
    }

    [Fact]
    public async Task CommitReply_UsesCurrentText_WhenDraftLagsBehind()
    {
        var tab = await OpenLabeled(1);
        await Add(tab, tab.Session!.Pages[0].Id, Note(tab, "kim", "root note"));
        using var vm = new CommentsViewModel(tab);
        var root = vm.Items[0];
        vm.ReplyCommand.Execute(root);
        root.Ui.Draft = "안녕하세";   // 마지막 글자가 아직 연결에 못 들어온 상태

        await vm.CommitReplyAsync(root, "안녕하세요");

        Assert.Equal(["root note", "안녕하세요"], vm.Items.Select(i => i.Contents));
        Assert.DoesNotContain(vm.Items, i => i.Ui.IsReplying);
    }

    [Fact]
    public async Task ReplyDraft_SurvivesRebuildOfItsPage_AndIsClearedAfterCommit()
    {
        var tab = await OpenLabeled(1);
        var page = tab.Session!.Pages[0].Id;
        await Add(tab, page, Note(tab, "kim", "root note"));
        using var vm = new CommentsViewModel(tab);
        var root = vm.Items[0];
        vm.ReplyCommand.Execute(root);
        root.Ui.Draft = "half typed";

        await Add(tab, page, Note(tab, "lee", "another", x: 200, y: 200));   // 같은 쪽 줄이 다시 만들어진다
        var rebuilt = vm.Items.Single(i => i.Contents == "root note");
        Assert.NotSame(root, rebuilt);
        Assert.True(rebuilt.Ui.IsReplying);
        Assert.Equal("half typed", rebuilt.Ui.Draft);

        await vm.CommitReplyAsync(rebuilt);
        Assert.DoesNotContain(vm.Items, i => i.Ui.IsReplying);
        Assert.Null(vm.DraftFor(rebuilt.Id!.Value));
        Assert.Contains(vm.Items, i => i.Depth == 1 && i.Contents == "half typed");

        // 취소하면 다시 만들어도 열리지 않는다
        var again = vm.Items.Single(i => i.Contents == "root note");
        vm.ReplyCommand.Execute(again);
        vm.CancelReply(again);
        await Add(tab, page, Note(tab, "lee", "third", x: 300, y: 300));
        Assert.DoesNotContain(vm.Items, i => i.Ui.IsReplying);
    }
}
