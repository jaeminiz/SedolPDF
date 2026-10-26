namespace PdfEditor.Appearance.Fonts;

/// <summary>앱 출력 폴더의 <c>assets/fonts/NotoSansKR-Regular.otf</c> 위치와 읽기.</summary>
public static class AppFontFile
{
    public const string RelativePath = "assets/fonts/NotoSansKR-Regular.otf";

    public static string PathIn(string baseDirectory) => Path.Combine(baseDirectory, "assets", "fonts", "NotoSansKR-Regular.otf");

    /// <summary>글꼴 파일 바이트. 파일이 없거나 읽을 수 없으면 빈 배열(패처가 글꼴이 필요할 때 예외를 낸다).</summary>
    public static byte[] ReadOrEmpty(string baseDirectory)
    {
        try
        {
            string p = PathIn(baseDirectory);
            return File.Exists(p) ? File.ReadAllBytes(p) : [];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    public static string SerifPathIn(string baseDirectory) => Path.Combine(baseDirectory, "assets", "fonts", "NotoSerifKR-Regular.otf");

    /// <summary>명조 글꼴 파일 바이트. 없거나 읽을 수 없으면 빈 배열(고딕으로 물러난다).</summary>
    public static byte[] ReadSerifOrEmpty(string baseDirectory)
    {
        try
        {
            string p = SerifPathIn(baseDirectory);
            return File.Exists(p) ? File.ReadAllBytes(p) : [];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>처음 부르면 읽고 이후에는 같은 바이트를 돌려주는 공급자(빈 결과는 캐시하지 않아 다음에 다시 시도).</summary>
    public static Func<byte[]> CachedProvider(string baseDirectory)
    {
        byte[]? cache = null;
        object gate = new();
        return () =>
        {
            lock (gate)
            {
                if (cache is not null) return cache;
                var data = ReadOrEmpty(baseDirectory);
                if (data.Length > 0) cache = data;
                return data;
            }
        };
    }
}
