using Avalonia;
using PdfEditor.App.Shell;

namespace PdfEditor.App.Hosting;

/// <summary>창 목록·탭 이동이 보는 창의 최소 모양(시험에서는 가짜 창으로 바꾼다).</summary>
public interface IHostWindow
{
    MainWindowViewModel ViewModel { get; }
    bool IsClosed { get; }
    /// <summary>최소화돼 있으면 되살리고 앞으로 가져온다.</summary>
    void BringToFront();
    /// <summary>탭 줄의 화면(물리 픽셀) 사각형. 보이지 않으면 null.</summary>
    PixelRect? TabStripScreenRect { get; }
}
