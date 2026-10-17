using System.Collections.Concurrent;
using System.Collections.Immutable;
using HarfBuzzSharp;

namespace PdfEditor.Appearance.Fonts;

/// <summary>셰이핑 결과 글리프 하나. 폭은 1/1000 em 단위.</summary>
public readonly record struct ShapedGlyph(int Gid, int Cluster, int Advance1000);

/// <summary>
/// 앱에 들어 있는 Noto Sans KR 하나. 전체 글꼴을 HarfBuzz에 올려 두고 글리프·CID·폭을 알려 준다.
/// HarfBuzz Blob은 <see cref="FontSubsetter.CreateBlob"/>(네이티브 메모리 복사)로만 만든다(S2 크래시 원인 방지).
/// HarfBuzz Face/Font는 스레드 간 공유가 안전하다고 보장하지 않으므로 모든 호출을 <c>lock</c>으로 직렬화한다.
/// </summary>
public sealed class AppFont : IDisposable
{
    private readonly object _lock = new();
    private readonly Blob _blob;
    private readonly Face _face;
    private readonly HarfBuzzSharp.Font _font;
    private readonly int[]? _cidByGid;
    private bool _disposed;

    internal ConcurrentDictionary<int, ImmutableArray<PathSeg>> OutlineCache { get; } = new();

    public byte[] Data { get; }
    public int UnitsPerEm { get; }
    public float Ascender1000 { get; }
    public float Descender1000 { get; }

    private AppFont(byte[] data)
    {
        Data = data;
        _cidByGid = CffCharset.GetCidByGid(data);
        _blob = FontSubsetter.CreateBlob(data);
        _face = new Face(_blob, 0);
        _font = new HarfBuzzSharp.Font(_face);
        UnitsPerEm = _face.UnitsPerEm;
        _font.SetScale(UnitsPerEm, UnitsPerEm);
        var ext = _font.GetFontExtentsForDirection(Direction.LeftToRight);
        Ascender1000 = ext.Ascender * 1000f / UnitsPerEm;
        Descender1000 = ext.Descender * 1000f / UnitsPerEm;
    }

    public static AppFont Load(string path) => FromBytes(File.ReadAllBytes(path));

    public static AppFont FromBytes(byte[] data)
    {
        if (data is null || data.Length == 0) throw new InvalidOperationException("Font data is empty.");
        return new AppFont(data);
    }

    /// <summary>글꼴에 있는 글자의 글리프 번호. 없으면(.notdef) null.</summary>
    public int? GetGlyph(int codepoint)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return _font.TryGetGlyph(codepoint, out uint g) && g != 0 ? (int)g : null;
        }
    }

    /// <summary>전체 글꼴의 CFF charset에서 본 CID. 이름 키 방식 CFF면 CID = 글리프 번호.</summary>
    public int GetCid(int gid) => _cidByGid is null ? gid : _cidByGid[gid];

    /// <summary>전체 글꼴이 CID 키 방식 CFF인가(아니면 CID = 글리프 번호).</summary>
    internal bool HasCidCharset => _cidByGid is not null;

    public int Advance1000(int gid)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return (int)Math.Round(_font.GetHorizontalGlyphAdvance((uint)gid) * 1000.0 / UnitsPerEm);
        }
    }

    /// <summary>한 줄(줄바꿈 없음)을 셰이핑한다. 직렬화되어 있다.</summary>
    public ImmutableArray<ShapedGlyph> Shape(string line)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            using var buf = new HarfBuzzSharp.Buffer();
            buf.AddUtf16(line);
            buf.GuessSegmentProperties();
            _font.Shape(buf);
            var infos = buf.GlyphInfos;
            var pos = buf.GlyphPositions;
            var b = ImmutableArray.CreateBuilder<ShapedGlyph>(infos.Length);
            for (int i = 0; i < infos.Length; i++)
                b.Add(new ShapedGlyph((int)infos[i].Codepoint, (int)infos[i].Cluster,
                    (int)Math.Round(pos[i].XAdvance * 1000.0 / UnitsPerEm)));
            return b.MoveToImmutable();
        }
    }

    /// <summary>HarfBuzz 네이티브 호출을 같은 잠금 안에서 하게 한다(윤곽선 추출용).</summary>
    internal T WithNativeFont<T>(Func<IntPtr, T> action)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return action(_font.Handle);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _font.Dispose();
            _face.Dispose();
            _blob.Dispose();
        }
    }
}
