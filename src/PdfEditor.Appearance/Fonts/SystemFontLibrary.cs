using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace PdfEditor.Appearance.Fonts;

/// <summary>설치된 글꼴 파일 안의 얼굴 하나. Key는 화면·AP·저장이 같은 AppFont를 찾는 글꼴 키(<c>sys:경로#번호</c>).</summary>
public sealed record SystemFontFace(string Path, OpenTypeFace Info)
{
    public const string KeyPrefix = "sys:";
    public string Key => $"{KeyPrefix}{Path}#{Info.FaceIndex}";
}

/// <summary>목록에 보이는 글꼴 가족 하나(얼굴 여러 개: 보통·굵게·기울임).</summary>
public sealed record SystemFontFamily(string Name, string DisplayName, bool Serif, ImmutableArray<SystemFontFace> Faces);

/// <summary>
/// 설치된 글꼴(0.12.0). 글꼴 폴더를 처음 필요할 때 한 번 훑어(표 머리만) 기억하고, 고른 얼굴의 파일은 처음 쓸 때 읽어 앱 실행 동안 둔다.
/// 포함이 금지된 글꼴·가변 글꼴·지원하지 않는 윤곽선은 목록에도, 이름 찾기에도 나오지 않는다(그런 이름은 호출자가 Noto로 대체한다).
/// 스레드 안전.
/// </summary>
public sealed class SystemFontLibrary
{
    private readonly Lazy<Index> _index;
    private readonly ConcurrentDictionary<string, Lazy<AppFont?>> _fonts = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="scan">얼굴 목록을 만드는 함수(시험은 고정 목록을 준다).</param>
    public SystemFontLibrary(Func<IEnumerable<SystemFontFace>> scan)
        => _index = new Lazy<Index>(() => new Index(scan()), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Windows 글꼴 폴더(시스템 + 사용자별 설치)를 훑는 라이브러리.</summary>
    public static SystemFontLibrary ForWindows() => new(() => Scan(DefaultDirectories()));

    public static IEnumerable<string> DefaultDirectories()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (!string.IsNullOrEmpty(win)) yield return win;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) yield return Path.Combine(local, "Microsoft", "Windows", "Fonts");
    }

    /// <summary>폴더들의 .ttf·.otf·.ttc 얼굴(쓸 수 없는 것도 포함; 거르는 것은 Index가 한다). 읽을 수 없는 폴더·파일은 건너뛴다.</summary>
    public static IEnumerable<SystemFontFace> Scan(IEnumerable<string> directories)
    {
        foreach (var dir in directories)
        {
            string[] files;
            try { files = Directory.Exists(dir) ? Directory.GetFiles(dir) : []; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files)
            {
                string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".ttf" or ".otf" or ".ttc")) continue;
                foreach (var face in OpenTypeInfo.ReadFaces(f)) yield return new SystemFontFace(f, face);
            }
        }
    }

    /// <summary>훑기를 이미 했는가(UI가 "읽는 중"을 보일지 정할 때).</summary>
    public bool IsLoaded => _index.IsValueCreated;

    /// <summary>쓸 수 있는 가족 목록(보이는 이름 순). 처음 부르면 폴더를 훑는다.</summary>
    public IReadOnlyList<SystemFontFamily> Families => _index.Value.Families;

    /// <summary>
    /// 이름(가족·전체·PostScript, 아무 언어, 공백·대소문자 무시)으로 얼굴을 찾고 굵기·기울임이 가장 가까운 것을 고른다. 쓸 수 없거나 없으면 null.
    /// </summary>
    public SystemFontFace? Find(string? name, bool bold, bool italic)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var idx = _index.Value;
        string key = Normalize(name);
        if (idx.ByName.TryGetValue(key, out var fam))
            return Closest(fam.Faces, bold, italic);
        // 전체·PostScript 이름(예: MalgunGothicBold)으로 가리키면 그 얼굴 자체.
        return idx.ByFaceName.TryGetValue(key, out var face) ? face : null;
    }

    /// <summary>이름이 가리키는 가족(목록에서 고른 상태를 보일 때). 쓸 수 없거나 없으면 null.</summary>
    public SystemFontFamily? FindFamily(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var idx = _index.Value;
        string key = Normalize(name);
        if (idx.ByName.TryGetValue(key, out var fam)) return fam;
        return idx.ByFaceName.TryGetValue(key, out var face) && idx.ByName.TryGetValue(Normalize(face.Info.Family), out var f2) ? f2 : null;
    }

    private static SystemFontFace Closest(ImmutableArray<SystemFontFace> faces, bool bold, bool italic)
    {
        int want = bold ? 700 : 400;
        return faces.OrderBy(f => f.Info.Italic == italic ? 0 : 1)
            .ThenBy(f => Math.Abs(f.Info.Weight - want))
            .ThenBy(f => f.Info.Weight)
            .First();
    }

    /// <summary>얼굴의 글꼴. 처음 부르면 파일을 읽는다. 파일이 없거나 바뀌어 포함이 금지되었으면 null(호출자가 대체).</summary>
    public AppFont? Load(SystemFontFace face)
        => _fonts.GetOrAdd(face.Key, _ => new Lazy<AppFont?>(() => LoadFile(face), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>글꼴 키(<c>sys:경로#번호</c>)의 글꼴. 이미 읽은 것만이 아니라 처음 보는 키도 파일에서 읽는다.</summary>
    public AppFont? FontForKey(string key)
    {
        if (!key.StartsWith(SystemFontFace.KeyPrefix, StringComparison.Ordinal)) return null;
        if (_fonts.TryGetValue(key, out var lazy)) return lazy.Value;
        int hash = key.LastIndexOf('#');
        if (hash < 0 || !int.TryParse(key.AsSpan(hash + 1), out int index)) return null;
        string path = key[SystemFontFace.KeyPrefix.Length..hash];
        var info = OpenTypeInfo.ReadFaces(path).FirstOrDefault(f => f.FaceIndex == index);
        return info is null ? null : Load(new SystemFontFace(path, info));
    }

    private static AppFont? LoadFile(SystemFontFace face)
    {
        try
        {
            byte[] data = File.ReadAllBytes(face.Path);
            // 목록을 만든 뒤 파일이 바뀌었을 수 있으므로 읽은 바이트로 다시 확인한다.
            if (OpenTypeInfo.ReadFace(data, face.Info.FaceIndex) is not { Usable: true }) return null;
            return AppFont.FromBytes(data, face.Info.FaceIndex);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    internal static string Normalize(string name) => new(name.Where(c => !char.IsWhiteSpace(c) && c is not ('-' or '_')).Select(char.ToLowerInvariant).ToArray());

    private sealed class Index
    {
        public IReadOnlyList<SystemFontFamily> Families { get; }
        public Dictionary<string, SystemFontFamily> ByName { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SystemFontFace> ByFaceName { get; } = new(StringComparer.Ordinal);

        public Index(IEnumerable<SystemFontFace> faces)
        {
            var usable = faces.Where(f => f.Info.Usable).ToList();
            var families = new List<SystemFontFamily>();
            // 같은 영어 가족 이름의 얼굴을 묶는다(파일이 사용자 폴더와 시스템 폴더에 다 있으면 먼저 찾은 것).
            foreach (var g in usable.GroupBy(f => Normalize(f.Info.Family)))
            {
                var faceList = g.GroupBy(f => (f.Info.Weight, f.Info.Italic)).Select(x => x.First()).ToImmutableArray();
                var first = faceList.OrderBy(f => f.Info.Italic ? 1 : 0).ThenBy(f => Math.Abs(f.Info.Weight - 400)).First();
                var fam = new SystemFontFamily(first.Info.Family, first.Info.DisplayFamily, first.Info.Serif, faceList);
                families.Add(fam);
                foreach (var f in faceList)
                {
                    ByName.TryAdd(Normalize(f.Info.Family), fam);
                    if (f.Info.LocalFamily is { } l) ByName.TryAdd(Normalize(l), fam);
                }
            }
            foreach (var f in usable)
                foreach (var n in f.Info.Names)
                    ByFaceName.TryAdd(Normalize(n), f);
            Families = families.OrderBy(f => f.DisplayName, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("ko-KR"), true)).ToList();
        }
    }
}
