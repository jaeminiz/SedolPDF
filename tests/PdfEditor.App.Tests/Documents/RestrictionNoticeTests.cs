using PdfEditor.App.Documents;
using PdfEditor.App.Settings;

namespace PdfEditor.App.Tests.Documents;

/// <summary>0.13.0: 권한 제한 안내 띠 — 탭에서만 닫기, 다시 표시 안 함(설정 저장), 설정으로 되돌리기.</summary>
[Collection("TabSettings")]
public sealed class RestrictionNoticeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-notice-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsStore _store;

    public RestrictionNoticeTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        DocumentTabViewModel.Settings = _store;
    }

    public void Dispose()
    {
        DocumentTabViewModel.Settings = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Default_NoticesAreVisible()
    {
        var tab = new DocumentTabViewModel("a");
        Assert.True(tab.NoticesVisible);
        Assert.True(DocumentTabViewModel.RestrictionNoticesEnabled);
    }

    [Fact]
    public void Dismiss_HidesOnlyThisTab_AndDoesNotTouchSettings()
    {
        var a = new DocumentTabViewModel("a");
        var b = new DocumentTabViewModel("b");
        var changed = new List<string?>();
        a.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        a.DismissNoticesCommand.Execute(null);

        Assert.False(a.NoticesVisible);
        Assert.True(b.NoticesVisible);
        Assert.Contains(nameof(DocumentTabViewModel.ShowEditRestricted), changed);
        Assert.Contains(nameof(DocumentTabViewModel.ShowAnnotationNotice), changed);
        Assert.NotEqual(true, _store.Load().HideRestrictionNotices);
        Assert.True(new DocumentTabViewModel("reopened").NoticesVisible);   // 다시 열면 또 뜬다
    }

    [Fact]
    public void HideForever_SavesSettingAndHidesEveryTab_ThenSettingTurnsItBackOn()
    {
        var a = new DocumentTabViewModel("a");
        var b = new DocumentTabViewModel("b");

        a.HideNoticesForeverCommand.Execute(null);

        Assert.True(_store.Load().HideRestrictionNotices);
        Assert.False(a.NoticesVisible);
        Assert.False(b.NoticesVisible);
        Assert.False(new DocumentTabViewModel("c").NoticesVisible);

        DocumentTabViewModel.SetRestrictionNoticesEnabled(true);   // 환경 설정에서 다시 켬

        Assert.NotEqual(true, _store.Load().HideRestrictionNotices);
        Assert.True(b.NoticesVisible);
        Assert.True(new DocumentTabViewModel("d").NoticesVisible);
    }

    [Fact]
    public void HidingNotices_DoesNotChangeTheRestrictionsThemselves()
    {
        var tab = new DocumentTabViewModel("a");   // 세션 없음: 제한 여부는 세션 권한에서 오고 안내 설정과 무관
        bool before = tab.CanAnnotate, edit = tab.CanEditContent;
        DocumentTabViewModel.SetRestrictionNoticesEnabled(false);
        Assert.Equal(before, tab.CanAnnotate);
        Assert.Equal(edit, tab.CanEditContent);
        Assert.False(tab.ShowEditRestricted);
    }
}
