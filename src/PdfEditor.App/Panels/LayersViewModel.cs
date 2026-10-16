using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.Document.Layers;

namespace PdfEditor.App.Panels;

/// <summary>레이어 한 칸. 처음 표시 상태만 보여 주고 바꿀 수 없다.</summary>
public sealed record LayerItem(string Name, bool IsVisibleInitially);

/// <summary>
/// 레이어 패널 모델 (목록만). V7: PDFium에 레이어 API가 없고, 표시/숨김 전환은 2026-10-15 사용자 결정으로 다음 버전에 미룬다.
/// 패널을 열 때 FilePath에서 다시 읽는다(새 문서·병합 문서는 파일이 없어 "레이어 없음").
/// 암호 PDF를 PDFsharp가 사용자 암호로 Import 모드에서 여는지는 검증 필요 — 못 열면 "읽을 수 없음" 안내.
/// </summary>
public sealed partial class LayersViewModel(DocumentTabViewModel tab) : ObservableObject
{
    private bool _loaded;

    public ObservableCollection<LayerItem> Items { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLayers))] private bool _hasLoaded;
    [ObservableProperty] private string? _message;

    public bool HasLayers => HasLoaded && Items.Count > 0;
    public string ToggleNote => Strings.Panel_LayerToggleUnavailable;

    public async Task LoadAsync()
    {
        if (_loaded || tab.Session is not { } s) return;
        _loaded = true;
        string? path = s.FilePath;
        if (path is null || !File.Exists(path))
        {
            Message = Strings.Panel_NoLayers;
            HasLoaded = true;
            return;
        }
        try
        {
            string? password = s.Password;
            var layers = await Task.Run(() => LayerReader.Read(File.ReadAllBytes(path), password));
            foreach (var l in layers) Items.Add(new LayerItem(l.Name, l.InitiallyVisible));
            Message = Items.Count == 0 ? Strings.Panel_NoLayers : null;
        }
        catch (Exception)
        {
            Message = Strings.Panel_LayersUnreadable;
        }
        HasLoaded = true;
        OnPropertyChanged(nameof(HasLayers));
    }
}
