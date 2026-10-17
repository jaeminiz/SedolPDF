using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PdfEditor.Document.Annotations;

/// <summary>
/// 패치 때 임시 파일에 넣을 바이트(그림 스탬프 이미지, 첨부 파일). 세션당 하나, 스레드 안전.
/// <see cref="Empty"/>는 항상 비어 있는 읽기 전용 인스턴스라 공유해도 안전하다.
/// </summary>
public sealed class AnnotationAssets
{
    public const int MaxImageBytes = 25 * 1024 * 1024;
    public const int MaxImagePixels = 16384;
    /// <summary>가로x세로 전체 화소 상한(약 50메가픽셀). 한 변 상한만으로는 16384x16384(268MP)가 통과해 디코딩 때 메모리가 터진다.</summary>
    public const long MaxImageTotalPixels = 50_000_000;
    public const int MaxFileBytes = 25 * 1024 * 1024;

    public static AnnotationAssets Empty { get; } = new(readOnly: true);

    private readonly bool _readOnly;
    private readonly object _gate = new();
    private readonly Dictionary<string, (byte[] Data, int W, int H, string Format)> _images = new();
    private readonly Dictionary<string, (string Name, byte[] Data)> _files = new();

    private readonly Action<byte[], string>? _imageValidator;

    public AnnotationAssets() { }

    /// <summary>
    /// imageValidator: 형식(png/jpeg)이 맞는 이미지를 실제로 저장 파일에 넣을 수 있는지 미리 시험하는 함수(실패하면 예외).
    /// 넣을 수 없는 이미지가 나중의 모든 저장·복구·인쇄를 깨지 않도록 추가할 때 거른다. Document 모듈은 PDF 이미지 처리를 모르므로 바깥에서 받는다.
    /// </summary>
    public AnnotationAssets(Action<byte[], string> imageValidator) => _imageValidator = imageValidator;
    private AnnotationAssets(bool readOnly) => _readOnly = readOnly;

    /// <summary>PNG·JPEG만 받는다. 같은 내용은 같은 id(SHA-256 앞 16자, 소문자 16진수).</summary>
    public string AddImage(byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        ThrowIfReadOnly();
        if (encoded.Length == 0 || encoded.Length > MaxImageBytes)
            throw new ArgumentException("Image size is out of range.", nameof(encoded));
        var info = ReadImageInfo(encoded)
            ?? throw new ArgumentException("Only PNG and JPEG images are supported.", nameof(encoded));
        if (info.W <= 0 || info.H <= 0 || info.W > MaxImagePixels || info.H > MaxImagePixels)
            throw new ArgumentException("Image dimensions are out of range.", nameof(encoded));

        if ((long)info.W * info.H > MaxImageTotalPixels)
            throw new ArgumentException($"Image is too large ({info.W}x{info.H}); the limit is {MaxImageTotalPixels / 1_000_000} megapixels.", nameof(encoded));
        if (_imageValidator is not null)
        {
            try { _imageValidator(encoded, info.Format); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                throw new ArgumentException($"This {info.Format.ToUpperInvariant()} image cannot be used in a PDF: {e.Message}", nameof(encoded), e);
            }
        }

        string id = HashId(encoded);
        lock (_gate) _images[id] = ((byte[])encoded.Clone(), info.W, info.H, info.Format);
        return id;
    }

    /// <summary>Format은 "png" 또는 "jpeg". 없는 id는 KeyNotFoundException.</summary>
    public (byte[] Data, int W, int H, string Format) GetImage(string id)
    {
        lock (_gate)
        {
            if (!_images.TryGetValue(id, out var v)) throw new KeyNotFoundException($"Unknown image id: {id}");
            return ((byte[])v.Data.Clone(), v.W, v.H, v.Format);
        }
    }

    public bool HasImage(string id) { lock (_gate) return _images.ContainsKey(id); }

    /// <summary>첨부 파일. id는 이름과 내용에서 만든다(SHA-256 앞 16자).</summary>
    public string AddFile(string name, byte[] data)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(data);
        ThrowIfReadOnly();
        if (data.Length > MaxFileBytes) throw new ArgumentException("File is too large.", nameof(data));
        string id = HashId(data, name);
        lock (_gate) _files[id] = (name, (byte[])data.Clone());
        return id;
    }

    public (string Name, byte[] Data) GetFile(string id)
    {
        lock (_gate)
        {
            if (!_files.TryGetValue(id, out var v)) throw new KeyNotFoundException($"Unknown file id: {id}");
            return (v.Name, (byte[])v.Data.Clone());
        }
    }

    public bool HasFile(string id) { lock (_gate) return _files.ContainsKey(id); }

    private void ThrowIfReadOnly()
    {
        if (_readOnly) throw new InvalidOperationException("This asset store is read-only.");
    }

    private static string HashId(byte[] data, string? name = null)
    {
        using var sha = SHA256.Create();
        if (name is not null)
        {
            var n = System.Text.Encoding.UTF8.GetBytes(name);
            sha.TransformBlock(n, 0, n.Length, null, 0);
            sha.TransformBlock([0], 0, 1, null, 0);
        }
        sha.TransformFinalBlock(data, 0, data.Length);
        return Convert.ToHexString(sha.Hash!)[..16].ToLowerInvariant();
    }

    private static (int W, int H, string Format)? ReadImageInfo(byte[] d)
    {
        // PNG: 8바이트 서명 + IHDR(폭·높이 big-endian)
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (d.Length >= 24 && d.AsSpan(0, 8).SequenceEqual(png) && d[12] == (byte)'I' && d[13] == (byte)'H' && d[14] == (byte)'D' && d[15] == (byte)'R')
            return (BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(20, 4)), "png");

        // JPEG: FFD8 + 마커들, SOFn(C0~CF 중 C4·C8·CC 제외)에서 크기
        if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8)
        {
            int i = 2;
            while (i + 4 <= d.Length)
            {
                if (d[i] != 0xFF) { i++; continue; }
                byte m = d[i + 1];
                if (m == 0xFF) { i++; continue; }
                if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { i += 2; continue; }
                if (m == 0xD9 || m == 0xDA) return null;
                int len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 2, 2));
                if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
                {
                    if (i + 9 > d.Length) return null;
                    return (BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 7, 2)), BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 5, 2)), "jpeg");
                }
                if (len < 2) return null;
                i += 2 + len;
            }
        }
        return null;
    }
}
