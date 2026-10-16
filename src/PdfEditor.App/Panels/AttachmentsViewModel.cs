using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;

namespace PdfEditor.App.Panels;

/// <summary>첨부 파일 한 칸: 이름과 크기(KB).</summary>
public sealed record AttachmentItem(int Index, string Name, long Size)
{
    public string SizeText => string.Format(Strings.Panel_AttachmentSize, Size > 0 ? Math.Max(Size / 1024.0, 0.1) : 0.0);
}

/// <summary>
/// 첨부 패널 모델. 목록은 처음 열 때 한 번 읽고, "저장"은 안전한 파일 이름(SafeFileName)을 제안한 뒤
/// 사용자가 고른 위치에만 쓴다. 모든 공개 멤버는 UI 스레드에서 쓴다.
/// </summary>
public sealed partial class AttachmentsViewModel(DocumentTabViewModel tab, IUserPrompts prompts) : ObservableObject
{
    private bool _loaded;

    public ObservableCollection<AttachmentItem> Items { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty))] private bool _hasLoaded;

    public bool IsEmpty => HasLoaded && Items.Count == 0;
    public string EmptyText => Strings.Panel_NoAttachments;

    public async Task LoadAsync()
    {
        if (_loaded || tab.Session is not { } s) return;
        _loaded = true;
        IReadOnlyList<PdfAttachmentInfo> list;
        try { list = await s.Thread.InvokeAsync(() => s.Document.GetAttachments()); }
        catch (Exception) { list = []; }
        foreach (var a in list) Items.Add(new AttachmentItem(a.Index, a.Name, a.Size));
        HasLoaded = true;
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>저장 위치를 묻고 첨부 내용을 쓴다. 취소하면 아무것도 하지 않고, 실패하면 오류 문구를 보인다.</summary>
    public async Task SaveAsync(AttachmentItem item)
    {
        if (tab.Session is not { } s) return;
        try
        {
            string? target = await prompts.PickSaveFileAsync(SafeFileName.From(item.Name));
            if (target is null) return;
            byte[] data = await s.Thread.InvokeAsync(() => s.Document.GetAttachmentData(item.Index));
            await File.WriteAllBytesAsync(target, data);
        }
        catch (Exception ex)
        {
            await prompts.ShowErrorAsync(ErrorMessages.For(ex));
        }
    }
}
