using Avalonia.Controls;
using PdfEditor.App.Dialogs;
using PdfEditor.App.Tests.Viewer.Annotations;
using Xunit;

namespace PdfEditor.App.Tests.Hosting;

/// <summary>대화상자 부모 창은 만들 때가 아니라 띄울 때 정한다(0.8.0 여러 창, 단계 1).</summary>
public class DialogOwnerTests
{
    [Fact]
    public void Prompts_ResolveOwnerAtCallTime()
    {
        var (owned1, owned2) = HeadlessUi.Run(() =>
        {
            var w1 = new Window { Width = 300, Height = 200 };
            var w2 = new Window { Width = 300, Height = 200 };
            w1.Show();
            w2.Show();
            Window current = w1;
            var prompts = new AvaloniaPrompts(() => current);
            current = w2;
            _ = prompts.ShowInfoAsync("x");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var result = (w1.OwnedWindows.Count, w2.OwnedWindows.Count);
            foreach (var d in w2.OwnedWindows.ToList()) d.Close();
            w2.Close();
            w1.Close();
            return result;
        });
        Assert.Equal(0, owned1);
        Assert.Equal(1, owned2);
    }
}
