using Pdf.Native;
using PdfEditor.Appearance;
using PdfEditor.Appearance.Patching;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Annotations.Reading;
using PdfEditor.Document.Commands;
using PdfEditor.Document.Saving;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Document.Tests.Annotations;

/// <summary>답글·검토 상태(Text + /IRT) 모델·저장·읽기·실행 취소 (M3b 계획 Task 1).</summary>
public class ThreadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-thread-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();
    private readonly PdfAppearanceSource _ap = new();
    private readonly PdfSharpAnnotationPatcher _patcher = new(() => []);

    private const string StateTemplate = "{0} 님이 상태를 {1}로 설정";   // 앱에서는 Strings 리소스에서 온다
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.FromHours(9));

    public ThreadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        _patcher.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string n) => Path.Combine(_dir, n);
    private SaveService Svc() => new([], _ap, _patcher);

    private async Task<DocumentSession> OpenBlankAsync()
    {
        TestPdfFactory.CreateBlank(P("blank.pdf"), 1, 200, 200);
        var s = await DocumentSession.OpenAsync(_t, P("blank.pdf"), null);
        await s.AnnotationsReady;
        return s;
    }

    private async Task<DocumentSession> OpenFixtureAsync(string name, Action<string> create, string? password = null)
    {
        create(P(name));
        var s = await DocumentSession.OpenAsync(_t, P(name), password);
        await s.AnnotationsReady;
        return s;
    }

    private static Annotation NewNote(DocumentSession s) => new()
    {
        Id = s.Annotations.NewId(), Kind = AnnotKind.Text, Rect = new PdfRectF(40, 120, 64, 144), Color = new PdfColor(255, 212, 0),
        Contents = "note", Author = "tester", Flags = PdfAnnotFlags.Print, CreatedAt = T0, ModifiedAt = T0,
    };

    private static IReadOnlyList<OriginalAnnotation> ReadBack(string pdf)
        => AnnotationReader.Read(File.ReadAllBytes(pdf), null, [0])[0];

    private static T Deref<T>(PdfItem? item) where T : PdfItem => (T)((item as PdfReference)?.Value ?? item)!;

    private static List<PdfDictionary> Annots(PdfSharp.Pdf.PdfDocument doc)
        => doc.Pages[0].Elements["/Annots"] is { } a ? [.. Deref<PdfArray>(a).Elements.Select(Deref<PdfDictionary>)] : [];

    private static string? Str(PdfDictionary d, string key)
        => Deref<PdfItem>(d.Elements[key]) is PdfString ps ? ps.Value : null;

    [Fact]
    public async Task Reply_SaveReopen_LinkedToParent()
    {
        await using var s = await OpenBlankAsync();
        var id = s.Pages[0].Id;
        var note = NewNote(s);
        var reply = ThreadAnnotations.NewReply(s.Annotations, note, "답글입니다", "reviewer", T0.AddMinutes(5));
        await s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(id, [new AnnotationChange(null, note), new AnnotationChange(null, reply)]));

        Assert.True(reply.NeedsPatch());
        Assert.False(note.NeedsPatch());

        for (int i = 0; i < 3; i++)   // 반복 저장해도 답글이 늘지 않는다
        {
            string target = P($"reply{i}.pdf");
            await Svc().SaveAsync(s, target);
            using var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import);
            var list = Annots(doc);
            var texts = list.Where(d => d.Elements.GetName("/Subtype") == "/Text").ToList();
            Assert.Equal(2, texts.Count);   // 메모 + 답글 (Popup은 별도 주석)
            var rd = Assert.Single(texts, d => d.Elements.ContainsKey("/IRT"));
            var nd = Assert.Single(texts, d => !d.Elements.ContainsKey("/IRT"));
            Assert.Same(nd, Deref<PdfDictionary>(rd.Elements["/IRT"]));
            Assert.Equal("/R", rd.Elements.GetName("/RT"));
            Assert.Equal(28, rd.Elements.GetInteger("/F"));
            Assert.Equal("답글입니다", Str(rd, "/Contents"));
            Assert.Equal("reviewer", Str(rd, "/T"));
            Assert.False(rd.Elements.ContainsKey("/State"));
            Assert.False(string.IsNullOrEmpty(Str(nd, "/NM")));   // 부모 /NM은 Flush가 보장
            // 빈 AP: 폼은 있지만 그리는 것이 없다
            var form = Deref<PdfDictionary>(Deref<PdfDictionary>(rd.Elements["/AP"]).Elements["/N"]);
            Assert.True(form.Stream is null || form.Stream.Value.All(b => b <= 0x20));
            Assert.Equal(1, list.Count(d => d.Elements.GetName("/Subtype") == "/Popup"));   // 메모의 Popup만 (답글에는 없음)
        }

        var back = ReadBack(P("reply2.pdf"));
        var br = back.Single(o => o.ReplyParentIndex is not null);
        Assert.Equal(ReadOnlyReason.None, br.Reason);
        Assert.Equal("답글입니다", br.Data!.Contents);
        Assert.Equal(back.Single(o => o.Data is { Kind: AnnotKind.Text } && o.ReplyParentIndex is null).Index, br.ReplyParentIndex);
    }

    [Fact]
    public async Task State_SaveReopen_HiddenFlagAndStateModel()
    {
        await using var s = await OpenBlankAsync();
        var id = s.Pages[0].Id;
        var note = NewNote(s);
        var st = ThreadAnnotations.NewState(s.Annotations, note, ReviewState.Accepted, "reviewer", T0.AddMinutes(5), StateTemplate, "승인");
        Assert.Equal("reviewer 님이 상태를 승인로 설정", st.Contents);
        await s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(id, [new AnnotationChange(null, note), new AnnotationChange(null, st)]));
        await Svc().SaveAsync(s, P("state.pdf"));

        using (var doc = PdfReader.Open(P("state.pdf"), PdfDocumentOpenMode.Import))
        {
            var sd = Assert.Single(Annots(doc), d => d.Elements.ContainsKey("/State"));
            Assert.Equal(30, sd.Elements.GetInteger("/F"));   // Hidden | Print | NoZoom | NoRotate
            Assert.Equal("Accepted", Str(sd, "/State"));
            Assert.Equal("Review", Str(sd, "/StateModel"));
            Assert.Equal("reviewer 님이 상태를 승인로 설정", Str(sd, "/Contents"));
            Assert.True(sd.Elements.ContainsKey("/IRT"));
            Assert.True(sd.Elements.ContainsKey("/AP"));
        }

        var back = ReadBack(P("state.pdf"));
        var bs = back.Single(o => o.Data?.State is not null);
        Assert.Equal(ReadOnlyReason.None, bs.Reason);   // Hidden이지만 상태 주석은 읽는다
        Assert.Equal(ReviewState.Accepted, bs.Data!.State);
    }

    [Fact]
    public async Task CurrentState_IsLatest()
    {
        await using var s = await OpenBlankAsync();
        var id = s.Pages[0].Id;
        var note = NewNote(s);
        var early = ThreadAnnotations.NewState(s.Annotations, note, ReviewState.Rejected, "a", T0.AddMinutes(1), StateTemplate, "거부");
        var late = ThreadAnnotations.NewState(s.Annotations, note, ReviewState.Completed, "a", T0.AddMinutes(9), StateTemplate, "완료");
        var mid = ThreadAnnotations.NewState(s.Annotations, note, ReviewState.Accepted, "a", T0.AddMinutes(5), StateTemplate, "승인");
        // 만든 순서와 시각 순서가 달라도 가장 늦은 시각이 현재 상태
        await s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(id,
            [new AnnotationChange(null, note), new AnnotationChange(null, early), new AnnotationChange(null, late), new AnnotationChange(null, mid)]));
        Assert.Equal(ReviewState.Completed, s.Annotations.CurrentState(id, note.Id));
        Assert.Equal(ReviewState.None, s.Annotations.CurrentState(id, early.Id));   // 상태 주석 자체엔 상태가 달리지 않았다
        var thread = s.Annotations.ThreadOf(id, note.Id);
        Assert.Equal([note.Id, early.Id, mid.Id, late.Id], thread.Select(a => a.Id));   // 뿌리, 상태(시각순)

        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Delete(id, [late]));
        Assert.Equal(ReviewState.Accepted, s.Annotations.CurrentState(id, note.Id));
    }

    [Fact]
    public async Task DeleteParent_RemovesRepliesAndStates_UndoRestoresAll()
    {
        await using var s = await OpenBlankAsync();
        var id = s.Pages[0].Id;
        var note = NewNote(s);
        var reply = ThreadAnnotations.NewReply(s.Annotations, note, "r1", "a", T0.AddMinutes(1));
        var nested = ThreadAnnotations.NewReply(s.Annotations, reply, "r2", "a", T0.AddMinutes(2));
        var st = ThreadAnnotations.NewState(s.Annotations, note, ReviewState.Rejected, "a", T0.AddMinutes(3), StateTemplate, "거부");
        await s.Commands.ExecuteAsync(new ChangeAnnotationsCommand(id,
            [new AnnotationChange(null, note), new AnnotationChange(null, reply), new AnnotationChange(null, nested), new AnnotationChange(null, st)]));
        await Svc().SaveAsync(s, P("before.pdf"));

        var thread = s.Annotations.ThreadOf(id, note.Id);
        Assert.Equal([note.Id, reply.Id, nested.Id, st.Id], thread.Select(a => a.Id));   // 뿌리, 답글(시각순; 답글의 답글 포함), 상태
        Assert.Equal(thread.Select(a => a.Id), s.Annotations.DeleteSetOf(id, note.Id).Select(a => a.Id));

        long v0 = s.Commands.Version;
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.DeleteThread(id, thread));
        Assert.Empty(s.Annotations.Get(id).Items);
        await Svc().SaveAsync(s, P("deleted.pdf"));
        Assert.Empty(ReadBack(P("deleted.pdf")));

        await s.Commands.UndoAsync();   // 실행 취소 한 번에 모두 돌아온다
        Assert.Equal(v0, s.Commands.Version);
        Assert.Equal(4, s.Annotations.Get(id).Items.Length);
        Assert.Equal([note.Id, reply.Id, nested.Id, st.Id], s.Annotations.ThreadOf(id, note.Id).Select(a => a.Id));
        await Svc().SaveAsync(s, P("restored.pdf"));
        using var doc = PdfReader.Open(P("restored.pdf"), PdfDocumentOpenMode.Import);
        Assert.Equal(3, Annots(doc).Count(d => d.Elements.ContainsKey("/IRT")));
    }

    [Fact]
    public async Task DeleteOriginalParent_RemovesOriginalRepliesAndStates_UndoRestoresAll()
    {
        await using var s = await OpenFixtureAsync("th.pdf", AnnotatedPdfFactory.CreateThreads);
        var id = s.Pages[0].Id;
        var note = s.Annotations.Get(id).Items.Single(v => v.Data.Name == "note-1").Data;
        var set = s.Annotations.DeleteSetOf(id, note.Id);
        Assert.Equal(5, set.Count);   // 메모, 답글, 답글의 답글, 상태 2

        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.DeleteThread(id, set));
        await Svc().SaveAsync(s, P("th-del.pdf"));
        using (var doc = PdfReader.Open(P("th-del.pdf"), PdfDocumentOpenMode.Import))
        {
            // 남는 것: Link와 그 읽기 전용 답글. 메모의 Popup과 읽기 전용 Marked 상태는 부모와 함께 지워진다.
            Assert.Equal(["/Link", "/Text"], Annots(doc).Select(d => d.Elements.GetName("/Subtype")));
        }

        await s.Commands.UndoAsync();
        Assert.Equal(5, s.Annotations.DeleteSetOf(id, note.Id).Count);
        await Svc().SaveAsync(s, P("th-undo.pdf"));
        using (var doc = PdfReader.Open(P("th-undo.pdf"), PdfDocumentOpenMode.Import))
            Assert.Equal(9, Annots(doc).Count);   // 원본 그대로 (Popup 포함)
    }

    [Fact]
    public async Task DeleteParentOnly_OrphanedOriginalReplies_AreDroppedFromTheFile()
    {
        // 앱이 ThreadOf 전체를 담지 않고 부모만 지워도 저장 파일에 잘못된 /IRT가 남지 않는다
        await using var s = await OpenFixtureAsync("th2.pdf", AnnotatedPdfFactory.CreateThreads);
        var id = s.Pages[0].Id;
        var note = s.Annotations.Get(id).Items.Single(v => v.Data.Name == "note-1").Data;
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Delete(id, [note]));
        await Svc().SaveAsync(s, P("th2-del.pdf"));
        using var doc = PdfReader.Open(P("th2-del.pdf"), PdfDocumentOpenMode.Import);
        Assert.Equal(["/Link", "/Text"], Annots(doc).Select(d => d.Elements.GetName("/Subtype")));
    }

    [Fact]
    public async Task OriginalRepliesFromFixture_NowEditable()
    {
        AnnotatedPdfFactory.CreateThreads(P("fx.pdf"));
        var back = ReadBack(P("fx.pdf"));
        Assert.Equal(ReadOnlyReason.None, back[2].Reason);
        Assert.Equal(0, back[2].ReplyParentIndex);
        Assert.Equal(ReadOnlyReason.None, back[3].Reason);
        Assert.Equal(ReviewState.Accepted, back[3].Data!.State);
        Assert.Equal(ReviewState.Rejected, back[4].Data!.State);
        Assert.Equal(ReadOnlyReason.Reply, back[6].Reason);          // 부모(Link)가 읽기 전용
        Assert.Equal(ReadOnlyReason.ReviewState, back[7].Reason);    // Marked 모델은 지원하지 않는다
        Assert.Equal(ReadOnlyReason.None, back[8].Reason);
        Assert.Equal(2, back[8].ReplyParentIndex);

        await using var s = await DocumentSession.OpenAsync(_t, P("fx.pdf"), null);
        await s.AnnotationsReady;
        var id = s.Pages[0].Id;
        var items = s.Annotations.Get(id).Items;
        Assert.Equal(5, items.Length);   // 메모, 답글, 상태 2, 답글의 답글
        var note = items.Single(v => v.Data.Name == "note-1").Data;
        var thread = s.Annotations.ThreadOf(id, note.Id);
        Assert.Equal(["note-1", "reply-1", "reply-2", "state-1", "state-2"], thread.Select(a => a.Name));
        Assert.Equal(ReviewState.Rejected, s.Annotations.CurrentState(id, note.Id));

        // 손대지 않으면 파일의 주석 수가 그대로다 (키·Popup 그대로)
        await Svc().SaveAsync(s, P("fx-same.pdf"));
        using (var doc = PdfReader.Open(P("fx-same.pdf"), PdfDocumentOpenMode.Import))
            Assert.Equal(9, Annots(doc).Count);

        // 답글 내용을 고친다: 모르는 키(/XKeep)·/IRT·/RT가 남고 중복이 없다
        var reply = items.Single(v => v.Data.Name == "reply-1").Data;
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Replace(id, [(reply, reply with { Contents = "고친 답글", ModifiedAt = T0 })]));
        for (int i = 0; i < 2; i++)
        {
            string target = P($"fx-edit{i}.pdf");
            await Svc().SaveAsync(s, target);
            using var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import);
            var list = Annots(doc);
            Assert.Equal(9, list.Count);
            var rd = list.Single(d => Str(d, "/NM") == "reply-1");
            Assert.Equal("고친 답글", Str(rd, "/Contents"));
            Assert.Equal("keep-me", Str(rd, "/XKeep"));
            Assert.Equal("note-1", Str(Deref<PdfDictionary>(rd.Elements["/IRT"]), "/NM"));
            Assert.Equal("/R", rd.Elements.GetName("/RT"));
            Assert.Equal(4, rd.Elements.GetInteger("/F"));
        }
    }

    [Fact]
    public async Task NewReply_ToOriginalParentWithoutNm_GetsStableNm_NoDuplicatesOnRepeatedSaves()
    {
        // Mixed 픽스처의 Text 메모([3])에는 /NM이 없다
        await using var s = await OpenFixtureAsync("mx.pdf", AnnotatedPdfFactory.CreateMixed);
        var id = s.Pages[0].Id;
        var note = s.Annotations.Get(id).Items.Single(v => v.Data.Contents == AnnotatedPdfFactory.MemoText).Data;
        Assert.Null(note.Name);
        var reply = ThreadAnnotations.NewReply(s.Annotations, note, "새 답글", "me", T0);
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(id, reply));
        var nms = new List<string?>();
        for (int i = 0; i < 2; i++)
        {
            string target = P($"mx-out{i}.pdf");
            await Svc().SaveAsync(s, target);
            using var doc = PdfReader.Open(target, PdfDocumentOpenMode.Import);
            var list = Annots(doc);
            Assert.Equal(14, list.Count);   // 13 + 새 답글 1
            var rd = list.Single(d => Str(d, "/Contents") == "새 답글");
            var parent = Deref<PdfDictionary>(rd.Elements["/IRT"]);
            Assert.Equal(AnnotatedPdfFactory.MemoText, Str(parent, "/Contents"));
            Assert.False(string.IsNullOrEmpty(Str(parent, "/NM")));
            nms.Add(Str(parent, "/NM"));
        }
        Assert.Equal(nms[0], nms[1]);
    }

    private sealed class ThrowingCommand : IAnnotationCommand
    {
        public void Execute(DocumentSession s) => throw new InvalidOperationException("boom");
        public void Undo(DocumentSession s) { }
    }

    private sealed class LogCommand(List<string> log, string name) : IAnnotationCommand
    {
        public void Execute(DocumentSession s) => log.Add("do " + name);
        public void Undo(DocumentSession s) => log.Add("undo " + name);
    }

    [Fact]
    public async Task CompositeCommand_FailureRollsBack()
    {
        await using var s = await OpenBlankAsync();
        var id = s.Pages[0].Id;
        var note = NewNote(s);
        var log = new List<string>();
        var cmd = new CompositeCommand([new LogCommand(log, "a"), ChangeAnnotationsCommand.Add(id, note), new LogCommand(log, "b"), new ThrowingCommand()]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Commands.ExecuteAsync(cmd));
        Assert.Empty(s.Annotations.Get(id).Items);   // 앞의 것이 되돌려졌다
        Assert.Equal(["do a", "do b", "undo b", "undo a"], log);
        Assert.False(s.Commands.CanUndo);

        // 정상: 실행은 순서대로, 되돌리기는 거꾸로
        log.Clear();
        var ok = new CompositeCommand([new LogCommand(log, "a"), ChangeAnnotationsCommand.Add(id, note), new LogCommand(log, "b")]);
        await s.Commands.ExecuteAsync(ok);
        Assert.Single(s.Annotations.Get(id).Items);
        await s.Commands.UndoAsync();
        Assert.Empty(s.Annotations.Get(id).Items);
        Assert.Equal(["do a", "do b", "undo b", "undo a"], log);
        await s.Commands.RedoAsync();
        Assert.Single(s.Annotations.Get(id).Items);
    }

    [Fact]
    public async Task EncryptedDoc_ReplyNotPatched_Reported()
    {
        await using var s = await OpenFixtureAsync("enc.pdf", p => AnnotatedPdfFactory.CreateEncryptedWithAnnotations(p, "pw"), "pw");
        var id = s.Pages[0].Id;
        var note = s.Annotations.Get(id).Items.Single(v => v.Data.Kind == AnnotKind.Text).Data;
        var reply = ThreadAnnotations.NewReply(s.Annotations, note, "암호 문서 답글", "me", T0);
        await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.Add(id, reply));

        // 패치를 못 하는 문서: 답글을 조용히 일반 메모로 쓰지 않고 저장·인쇄·복구 전에 알린다
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Thread.InvokeAsync(() => s.FlushIfNeeded(_ap)));
        Assert.Contains("reply", ex.Message);
        var saveEx = await Assert.ThrowsAsync<SaveException>(() => Svc().SaveAsync(s, P("enc-out.pdf")));
        Assert.IsType<InvalidOperationException>(saveEx.InnerException);
        Assert.False(File.Exists(P("enc-out.pdf")));

        // 지우면 다시 저장할 수 있다
        await s.Commands.UndoAsync();
        await Svc().SaveAsync(s, P("enc-ok.pdf"));
        Assert.True(File.Exists(P("enc-ok.pdf")));
    }

    [Fact]
    public async Task EncryptedDoc_OriginalReplies_StayReadOnly()
    {
        AnnotatedPdfFactory.CreateThreads(P("thx.pdf"));
        using (var doc = PdfReader.Open(P("thx.pdf"), PdfDocumentOpenMode.Modify))
        {
            doc.SecuritySettings.UserPassword = "pw";
            doc.SecuritySettings.OwnerPassword = "pw-owner";
            doc.Save(P("thx-enc.pdf"));
        }
        await using var s = await DocumentSession.OpenAsync(_t, P("thx-enc.pdf"), "pw");
        await s.AnnotationsReady;
        var items = s.Annotations.Get(s.Pages[0].Id).Items;
        Assert.DoesNotContain(items, v => v.Data.InReplyTo is not null);   // 답글·상태는 모델에 없다 (PDFium이 원본 모양으로 그린다)
        Assert.Contains(items, v => v.Data.Name == "note-1");
        await Svc().SaveAsync(s, P("thx-out.pdf"));   // 아무것도 안 바꿨으니 저장은 된다
    }

    [Fact]
    public async Task EncryptedDoc_DeleteOriginalParent_RemovesItsUnmodeledReplies()
    {
        // 암호 문서: 원본 답글·상태는 모델에 없다. 부모(note-1)를 지우면 저장 때 그 답글·상태·Popup도 파일에서 지워져야 한다(고아 방지, ComputeRemoval).
        AnnotatedPdfFactory.CreateThreads(P("thc.pdf"));
        using (var doc = PdfReader.Open(P("thc.pdf"), PdfDocumentOpenMode.Modify))
        {
            doc.SecuritySettings.UserPassword = "pw";
            doc.SecuritySettings.OwnerPassword = "pw-owner";
            doc.Save(P("thc-enc.pdf"));
        }
        await using (var s = await DocumentSession.OpenAsync(_t, P("thc-enc.pdf"), "pw"))
        {
            await s.AnnotationsReady;
            var id = s.Pages[0].Id;
            var note = s.Annotations.Get(id).Items.Single(v => v.Data.Name == "note-1").Data;
            await s.Commands.ExecuteAsync(ChangeAnnotationsCommand.DeleteThread(id, s.Annotations.DeleteSetOf(id, note.Id)));
            await Svc().SaveAsync(s, P("thc-out.pdf"));
        }
        using var back = PdfReader.Open(P("thc-out.pdf"), "pw", PdfDocumentOpenMode.Import);
        var annots = back.Pages[0].Annotations;
        var names = new List<string?>();
        for (int i = 0; i < annots.Count; i++) names.Add(annots[i].Elements.GetString("/NM"));
        // 남는 것: 링크와 그 위의 읽기 전용 답글(다른 부모). 부모를 지운 스레드(note-1, reply-1, reply-2, state-1~3, Popup)는 없다.
        Assert.DoesNotContain(names, n => n is "note-1" or "reply-1" or "reply-2" or "state-1" or "state-2" or "state-3");
        Assert.Equal(2, annots.Count);
    }
}
