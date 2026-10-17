using System.Diagnostics;
using System.IO;
using System.Reflection;
using Avalonia.Controls;
using PdfEditor.App.Resources;

namespace PdfEditor.App.Dialogs;

/// <summary>앱 이름·버전과 "타사 고지 보기"(출력 폴더의 THIRD-PARTY-NOTICES.md를 기본 프로그램으로 엶).</summary>
public sealed partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        Title = Strings.Dlg_AboutTitle;
        NameText.Text = Strings.App_Name;
        VersionText.Text = string.Format(Strings.Dlg_AboutVersion, VersionString());
        AuthorsText.Text = Strings.About_Authors;
        Contact1Text.Text = Strings.About_Contact1;
        Contact2Text.Text = Strings.About_Contact2;
        NoticesButton.Content = Strings.Dlg_AboutNotices;
        CloseButton.Content = Strings.Dlg_AboutClose;
        NoticesButton.Click += (_, _) => OpenNotices();
        CloseButton.Click += (_, _) => Close();
    }

    public static string VersionString() =>
        typeof(AboutDialog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AboutDialog).Assembly.GetName().Version?.ToString(3) ?? "?";

    public static string NoticesPath => Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");

    private void OpenNotices()
    {
        try
        {
            if (!File.Exists(NoticesPath)) throw new FileNotFoundException(NoticesPath);
            Process.Start(new ProcessStartInfo(NoticesPath) { UseShellExecute = true });
            NoticeStatus.IsVisible = false;
        }
        catch (Exception)
        {
            NoticeStatus.Text = Strings.Dlg_AboutNoticesMissing;
            NoticeStatus.IsVisible = true;
        }
    }
}
