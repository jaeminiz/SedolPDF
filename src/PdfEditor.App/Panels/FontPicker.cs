using Avalonia.Controls;
using Avalonia.Threading;
using PdfEditor.App.Documents;
using PdfEditor.App.Resources;
using PdfEditor.Appearance.Fonts;
using PdfEditor.Document.Annotations;

namespace PdfEditor.App.Panels;

/// <summary>
/// 속성 패널·서식 탭의 글꼴 상자(0.12.0): 맨 위 고딕·명조(번들 Noto), 구분선, 설치된 글꼴(가나다·ABC순).
/// 설치된 글꼴 목록은 처음 펼칠 때 백그라운드에서 읽어 채운다. 고른 글꼴이 이 PC에 없으면(또는 포함 금지라 목록에 없으면) "이름 (이 PC에 없어 …)" 항목을 잠시 보인다.
/// 항목 Tag: TextFontFamily(고딕/명조) · SystemFontFamily(설치된 글꼴) · string(목록에 없는 이름, 고를 수 없음).
/// </summary>
internal static class FontPicker
{
    private static SystemFontLibrary? Library => DocumentTabViewModel.Appearance?.SystemFonts;

    public static void Init(ComboBox box, Func<bool> syncing, Func<TextFontFamily, Task> pickDefault, Func<SystemFontFamily, Task> pickSystem)
    {
        box.Items.Add(new ComboBoxItem { Content = Strings.Font_Sans, Tag = TextFontFamily.Sans });
        box.Items.Add(new ComboBoxItem { Content = Strings.Font_Serif, Tag = TextFontFamily.Serif });
        box.SelectionChanged += (_, _) =>
        {
            if (syncing() || _filling) return;
            switch ((box.SelectedItem as ComboBoxItem)?.Tag)
            {
                case TextFontFamily f: _ = pickDefault(f); break;
                case SystemFontFamily s: _ = pickSystem(s); break;
            }
        };
        box.DropDownOpened += (_, _) => _ = FillAsync(box);
    }

    // 목록을 채우는 동안 바뀌는 선택은 사용자가 고른 것이 아니다.
    private static bool _filling;

    private const string SeparatorTag = "\u0001separator";

    private static bool Filled(ComboBox box) => box.Items.OfType<ComboBoxItem>().Any(i => Equals(i.Tag, SeparatorTag));

    /// <summary>설치된 글꼴 항목을 (아직 없으면) 채운다. 훑기는 UI 스레드 밖에서.</summary>
    private static async Task FillAsync(ComboBox box)
    {
        if (Library is not { } lib || Filled(box)) return;
        IReadOnlyList<SystemFontFamily> families;
        try { families = lib.IsLoaded ? lib.Families : await Task.Run(() => lib.Families); }
        catch (Exception) { return; }   // 목록을 못 읽으면 고딕·명조만
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (Filled(box) || families.Count == 0) return;
            _filling = true;
            try
            {
                var keep = box.SelectedItem as ComboBoxItem;
                string? keepName = (keep?.Tag as SystemFontFamily)?.Name;
                // Select가 먼저 넣어 둔 가족 하나는 지우고 전체 목록으로 바꾼다.
                foreach (var one in box.Items.OfType<ComboBoxItem>().Where(i => i.Tag is SystemFontFamily).ToList()) box.Items.Remove(one);
                box.Items.Add(new ComboBoxItem { Content = new Separator { Margin = new Avalonia.Thickness(0, 2) }, Tag = SeparatorTag, IsEnabled = false, Focusable = false });
                ComboBoxItem? reselect = null;
                foreach (var f in families)
                {
                    var item = new ComboBoxItem { Content = f.DisplayName, Tag = f };
                    box.Items.Add(item);
                    if (f.Name == keepName) reselect = item;
                }
                box.SelectedItem = reselect ?? (keepName is null ? keep : null);
            }
            finally { _filling = false; }
        });
    }

    /// <summary>문서에 들어 있는 원본 글꼴 이름을 그대로 보인다(고를 수 없는 임시 항목, 0.12.0 텍스트 편집). _syncing 안에서 부른다.</summary>
    public static void ShowName(ComboBox box, string name)
    {
        foreach (var stale in box.Items.OfType<ComboBoxItem>().Where(i => i.Tag is string s && s != SeparatorTag).ToList()) box.Items.Remove(stale);
        var item = new ComboBoxItem { Content = string.IsNullOrEmpty(name) ? "?" : name, Tag = name ?? "" };
        box.Items.Insert(2, item);
        box.SelectedItem = item;
    }

    /// <summary>모델 값에 맞는 항목을 고른다(이벤트를 막는 _syncing 안에서 부른다).</summary>
    public static void Select(ComboBox box, TextFontFamily family, string? fontName)
    {
        foreach (var stale in box.Items.OfType<ComboBoxItem>().Where(i => i.Tag is string s && s != SeparatorTag).ToList()) box.Items.Remove(stale);
        if (string.IsNullOrEmpty(fontName))
        {
            box.SelectedIndex = (int)family;
            return;
        }
        if (Library is { IsLoaded: true } lib && lib.FindFamily(fontName) is { } fam)
        {
            var item = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is SystemFontFamily s && s.Name == fam.Name);
            if (item is null)
            {
                // 목록을 아직 펼치지 않았으면 그 가족 하나만 먼저 넣어 둔다(펼칠 때 나머지가 채워진다).
                item = new ComboBoxItem { Content = fam.DisplayName, Tag = fam };
                box.Items.Add(item);
            }
            box.SelectedItem = item;
            return;
        }
        // 이 PC에 없거나 포함 금지 글꼴: 이름은 문서에 남고 그리기는 고딕/명조로 대체된다. 목록이 아직 없으면 판단하지 않고 이름만 보인다.
        string text = Library is { IsLoaded: true } ? string.Format(Strings.Font_Missing, fontName) : fontName;
        var missing = new ComboBoxItem { Content = text, Tag = fontName };
        box.Items.Insert(2, missing);
        box.SelectedItem = missing;
    }
}
