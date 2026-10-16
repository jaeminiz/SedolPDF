using CommunityToolkit.Mvvm.ComponentModel;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;

namespace PdfEditor.App.Panels;

/// <summary>
/// 문서 속성 패널 모델 (주석 선택 기능이 없는 M1에서는 문서 속성만 보인다).
/// 쪽 수·현재 쪽 크기는 탭을 따라가고, 버전·메타데이터는 한 번 전용 스레드에서 읽는다.
/// </summary>
public sealed partial class PropertiesViewModel : ObservableObject
{
    private readonly DocumentTabViewModel _tab;

    [ObservableProperty] private string _pdfVersion = Strings.Prop_Unknown;
    [ObservableProperty] private string _title = Strings.Prop_Unknown;
    [ObservableProperty] private string _author = Strings.Prop_Unknown;
    [ObservableProperty] private string _creator = Strings.Prop_Unknown;
    [ObservableProperty] private string _producer = Strings.Prop_Unknown;

    public PropertiesViewModel(DocumentTabViewModel tab)
    {
        _tab = tab;
        tab.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(DocumentTabViewModel.PageCount): OnPropertyChanged(nameof(PageCount)); break;
                case nameof(DocumentTabViewModel.PageWidthMm):
                case nameof(DocumentTabViewModel.PageHeightMm): OnPropertyChanged(nameof(PageSizeText)); break;
                case nameof(DocumentTabViewModel.Title): OnPropertyChanged(nameof(FileName)); break;
            }
        };
    }

    public string FileName => _tab.Session?.DisplayName ?? Strings.Prop_Unknown;
    public int PageCount => _tab.PageCount;
    public string PageSizeText => string.Format(Strings.Prop_PageSizeValue, _tab.PageWidthMm, _tab.PageHeightMm);
    public string Encrypted => YesNo(_tab.Session?.IsEncrypted == true);
    public string Repaired => YesNo(_tab.Session?.WasRepaired == true);
    public string PrintPermission => Allowed(_tab.Session?.Permissions.CanPrint ?? true);
    public string EditPagesPermission => Allowed(_tab.Session?.Permissions.CanEditPages ?? true);
    public string CopyPermission => Allowed(_tab.Session?.Permissions.CanCopy ?? true);

    /// <summary>PDFium FileVersion(17 = PDF 1.7) → "1.7". 알 수 없으면 대시.</summary>
    public static string FormatVersion(int? raw) => raw is { } v ? $"{v / 10}.{v % 10}" : Strings.Prop_Unknown;

    private static string YesNo(bool b) => b ? Strings.Prop_Yes : Strings.Prop_No;
    private static string Allowed(bool b) => b ? Strings.Prop_Allowed : Strings.Prop_Denied;
    private static string OrDash(string? s) => string.IsNullOrWhiteSpace(s) ? Strings.Prop_Unknown : s;

    private bool _loaded;

    public async Task LoadAsync()
    {
        if (_loaded || _tab.Session is not { } s) return;
        _loaded = true;
        try
        {
            var (version, title, author, creator, producer) = await s.Thread.InvokeAsync(() =>
            {
                var d = s.Document;
                return (d.FileVersion, d.GetMetaText("Title"), d.GetMetaText("Author"),
                        d.GetMetaText("Creator"), d.GetMetaText("Producer"));
            });
            PdfVersion = FormatVersion(version);
            Title = OrDash(title);
            Author = OrDash(author);
            Creator = OrDash(creator);
            Producer = OrDash(producer);
        }
        catch (Exception)
        {
            // 속성을 못 읽어도 나머지 값은 보인다 (대시로 남김).
        }
        OnPropertyChanged(string.Empty);
    }
}
