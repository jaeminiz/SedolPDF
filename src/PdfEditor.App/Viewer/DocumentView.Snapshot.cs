using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.Document;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;

namespace PdfEditor.App.Viewer;

/// <summary>
/// 스냅숏 도구(홈 탭, 2026-10-20 사용자 요청): 한 쪽 안에서 끌어 고른 영역을 그림으로 클립보드에 넣는다.
/// 그림은 <see cref="SnapshotRenderer"/>가 만든다(화면과 같은 주석 그리기). 끝나면 화면 아래에 잠깐 알림을 보인다.
/// 문서 권한이 내용 복사를 막으면 텍스트 복사와 같이 안내만 한다. 실제 마우스 입력은 직접 확인한 것만 보고서에 적는다(검증 필요).
/// </summary>
public sealed partial class DocumentView
{
    private int _snapPage = -1;
    private Point _snapStart, _snapEnd;
    private string? _toast;
    private DispatcherTimer? _toastTimer;

    /// <summary>시험용: 마지막 스냅숏 결과(그림 크기). 클립보드가 없으면 그림만 만들고 여기에 남긴다.</summary>
    public PixelSize? LastSnapshotSize { get; private set; }

    private bool HandleSnapshotPressed(PointerPressedEventArgs e)
    {
        var st = _state; var lay = _layout;
        if (st is null || lay is null || st.Tab.Tool != ToolKind.Snapshot) return false;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return false;
        e.Handled = true;
        var pos = e.GetPosition(this);
        if (lay.HitTest(pos) is not { } hit) return true;
        _snapPage = hit.Page;
        _snapStart = _snapEnd = pos;
        e.Pointer.Capture(this);
        InvalidateVisual();
        return true;
    }

    private bool HandleSnapshotMoved(PointerEventArgs e)
    {
        if (_snapPage < 0 || _layout is not { } lay) return false;
        // 고른 영역은 누른 쪽 안으로 제한한다
        var pr = lay.GetPageRect(_snapPage);
        var p = e.GetPosition(this);
        _snapEnd = new Point(Math.Clamp(p.X, pr.Left, pr.Right), Math.Clamp(p.Y, pr.Top, pr.Bottom));
        InvalidateVisual();
        return true;
    }

    private bool HandleSnapshotReleased(PointerReleasedEventArgs e)
    {
        if (_snapPage < 0) return false;
        int pageIndex = _snapPage;
        _snapPage = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
        var st = _state; var lay = _layout;
        var r = new Rect(_snapStart, _snapEnd).Normalize();
        if (st is null || lay is null || pageIndex >= _layoutPages.Length) return true;
        var info = _layoutPages[pageIndex];
        if (!TryGetGeom(st, info, out var g)) return true;
        if (r.Width < 3 && r.Height < 3)
        {
            // 끌지 않고 그냥 누르면 그 쪽 전체(2026-10-20 사용자 요청)
            _ = CopySnapshotAsync(st, info.Id, PdfRectF.FromLtrbRectangle(g.Bounds));
            return true;
        }
        if (r.Width < 3 || r.Height < 3) return true;
        var a = ToPdf(g, pageIndex, r.TopLeft, lay.Scale);
        var b = ToPdf(g, pageIndex, r.BottomRight, lay.Scale);
        var region = new PdfRectF(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        _ = CopySnapshotAsync(st, info.Id, region);
        return true;
    }

    private async Task CopySnapshotAsync(ViewState st, PageId page, PdfRectF region)
    {
        if (!st.Session.Permissions.CanCopy) { st.Tab.RaiseNotice(Strings.Msg_CopyRestricted); return; }
        try
        {
            using var bmp = await SnapshotRenderer.RenderAsync(st.Session, page, region);
            if (bmp is null) return;
            LastSnapshotSize = bmp.PixelSize;
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await cb.SetBitmapAsync(bmp);
            ShowToast(Strings.Msg_SnapshotCopied);
        }
        catch (Exception) { st.Tab.RaiseNotice(Strings.Msg_SnapshotFailed); }   // 쪽이 지워졌거나 클립보드를 다른 프로그램이 잡고 있음
    }

    private void ShowToast(string text)
    {
        _toast = text;
        _toastTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _toastTimer.Stop();
        _toastTimer.Tick -= OnToastTick;
        _toastTimer.Tick += OnToastTick;
        _toastTimer.Start();
        InvalidateVisual();
    }

    private void OnToastTick(object? sender, EventArgs e)
    {
        _toastTimer?.Stop();
        _toast = null;
        InvalidateVisual();
    }

    private static readonly IPen SnapPen = new Pen(new SolidColorBrush(Color.Parse("#005FB8")), 1, new DashStyle([4, 3], 0));
    private static readonly IBrush SnapFill = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x5F, 0xB8));
    private static readonly IBrush ToastBack = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x20));

    private void DrawSnapshotOverlay(DrawingContext ctx)
    {
        if (_snapPage >= 0)
            ctx.DrawRectangle(SnapFill, SnapPen, new Rect(_snapStart, _snapEnd).Normalize());
        if (_toast is { } t)
        {
            var ft = new FormattedText(t, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 13, Brushes.White);
            var vp = Viewport;
            var box = new Rect(vp.X + (vp.Width - ft.Width) / 2 - 14, vp.Bottom - ft.Height - 40, ft.Width + 28, ft.Height + 14);
            ctx.DrawRectangle(ToastBack, null, box, 6, 6);
            ctx.DrawText(ft, new Point(box.X + 14, box.Y + 7));
        }
    }
}
