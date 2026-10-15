using CommunityToolkit.Mvvm.Input;
using PdfEditor.App.Shell;

namespace PdfEditor.App.Tests.Shell;

public class CommandRegistryTests
{
    private static AppCommand Cmd(string id, string label, bool enabled = true) =>
        new(id, label, new RelayCommand(() => { }, () => enabled), null, null);

    // 테스트 소스에도 한글 리터럴 가드는 적용되지 않는다(src/PdfEditor.App만 검사).
    private static CommandRegistry Sample()
    {
        var r = new CommandRegistry();
        r.Register(Cmd("page.delete", "페이지 삭제"));
        r.Register(Cmd("page.rotate", "페이지 회전"));
        r.Register(Cmd("file.open", "열기"));
        return r;
    }

    [Fact]
    public void Registry_Search_KoreanSubstring()
    {
        var r = Sample();
        Assert.Equal(2, r.Search("페이지").Count);
        Assert.Single(r.Search("삭제"));
    }

    [Fact]
    public void Registry_Search_Empty_ReturnsNone()
    {
        var r = Sample();
        Assert.Empty(r.Search(""));
        Assert.Empty(r.Search("   "));
    }

    [Fact]
    public void Registry_Search_DisabledLast()
    {
        var r = new CommandRegistry();
        r.Register(Cmd("a", "Page A", enabled: false));
        r.Register(Cmd("b", "Page B"));
        r.Register(Cmd("c", "page C", enabled: false));
        r.Register(Cmd("d", "PAGE D"));
        Assert.Equal(["b", "d", "a", "c"], r.Search("page").Select(c => c.Id));
    }

    [Fact]
    public void Registry_Find_And_DuplicateId()
    {
        var r = Sample();
        Assert.NotNull(r.Find("file.open"));
        Assert.Null(r.Find("nope"));
        Assert.Throws<ArgumentException>(() => r.Register(Cmd("file.open", "again")));
    }
}
