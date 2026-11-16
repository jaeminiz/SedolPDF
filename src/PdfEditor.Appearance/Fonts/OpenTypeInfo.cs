using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace PdfEditor.Appearance.Fonts;

/// <summary>윤곽선 종류. 0.12.0은 TrueType(glyf)와 CID 방식 CFF만 PDF에 넣는다.</summary>
public enum FontOutline { TrueType, CffCid, CffNameKeyed, Cff2, Unknown }

/// <summary>
/// 글꼴 파일 안 얼굴(face) 하나의 머리 정보(0.12.0 시스템 글꼴). 표 머리(name·OS/2·fvar·CFF 첫 부분)만 읽고 글리프는 읽지 않는다.
/// Names: 가족(16/1)·전체(4)·PostScript(6) 이름을 모든 언어로 모은 것(찾기용). Family: 영어 가족 이름, LocalFamily: 한국어 가족 이름(없으면 null).
/// </summary>
public sealed record OpenTypeFace(
    int FaceIndex, string Family, string? LocalFamily, string? PostScriptName, ImmutableArray<string> Names,
    int Weight, bool Italic, bool Serif, int? FsType, FontOutline Outline, bool Variable)
{
    /// <summary>한글 이름이 있으면 한글, 없으면 영어 가족 이름.</summary>
    public string DisplayFamily => LocalFamily ?? Family;

    public bool IsBold => Weight >= 600;

    /// <summary>PDF에 서브셋으로 넣어 쓸 수 있는가: 포함 허용(fsType) + 지원하는 윤곽선 + 가변 아님.</summary>
    public bool Usable => FsType is int fs && AppFontCatalog.FsTypeAllows(fs)
        && Outline is FontOutline.TrueType or FontOutline.CffCid && !Variable;
}

/// <summary>OpenType·TrueType·글꼴 모음(.ttc) 머리 읽기. 잘못된 파일은 빈 목록(예외 없음).</summary>
public static class OpenTypeInfo
{
    private const ushort LangKorean = 0x0412, LangEnglishUs = 0x0409;

    /// <summary>파일의 모든 얼굴. 읽을 수 없으면 빈 목록.</summary>
    public static IReadOnlyList<OpenTypeFace> ReadFaces(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            return ReadFaces(fs);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    public static IReadOnlyList<OpenTypeFace> ReadFaces(Stream s)
    {
        try
        {
            var head = ReadAt(s, 0, 12);
            if (head is null) return [];
            if (head[0] == 't' && head[1] == 't' && head[2] == 'c' && head[3] == 'f')
            {
                uint n = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8));
                if (n == 0 || n > 64) return [];
                var offs = ReadAt(s, 12, (int)n * 4);
                if (offs is null) return [];
                var list = new List<OpenTypeFace>();
                for (int i = 0; i < n; i++)
                    if (ReadFace(s, BinaryPrimitives.ReadUInt32BigEndian(offs.AsSpan(i * 4)), i) is { } f) list.Add(f);
                return list;
            }
            return ReadFace(s, 0, 0) is { } one ? [one] : [];
        }
        catch (Exception e) when (e is IOException or ArgumentException or IndexOutOfRangeException or ArgumentOutOfRangeException or EndOfStreamException)
        {
            return [];
        }
    }

    /// <summary>메모리에 있는 글꼴 바이트의 얼굴 하나.</summary>
    public static OpenTypeFace? ReadFace(byte[] data, int faceIndex)
    {
        var faces = ReadFaces(new MemoryStream(data, writable: false));
        return faces.FirstOrDefault(f => f.FaceIndex == faceIndex);
    }

    private static OpenTypeFace? ReadFace(Stream s, uint offset, int index)
    {
        var dir = ReadAt(s, offset, 12);
        if (dir is null) return null;
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(dir.AsSpan(4));
        if (numTables is 0 or > 200) return null;
        var recs = ReadAt(s, offset + 12, numTables * 16);
        if (recs is null) return null;
        var tables = new Dictionary<string, (uint Off, uint Len)>(StringComparer.Ordinal);
        for (int i = 0; i < numTables; i++)
        {
            var r = recs.AsSpan(i * 16);
            string tag = Encoding.ASCII.GetString(r[..4]);
            tables[tag] = (BinaryPrimitives.ReadUInt32BigEndian(r[8..]), BinaryPrimitives.ReadUInt32BigEndian(r[12..]));
        }
        if (!tables.TryGetValue("name", out var nameT)) return null;
        var name = ReadAt(s, nameT.Off, (int)Math.Min(nameT.Len, 1 << 20));
        if (name is null) return null;
        var names = ReadNames(name);

        string? Pick(int id, ushort lang) => names.FirstOrDefault(x => x.Id == id && x.Lang == lang).Text;
        string? AnyOf(int id) => Pick(id, LangEnglishUs) ?? names.FirstOrDefault(x => x.Id == id && x.Text is not null).Text;
        string? family = AnyOf(16) ?? AnyOf(1);
        if (string.IsNullOrWhiteSpace(family)) return null;
        string? local = Pick(16, LangKorean) ?? Pick(1, LangKorean);
        if (local == family) local = null;

        int weight = 400, familyClass = 0;
        bool italic = false;
        int? fsType = null;
        if (tables.TryGetValue("OS/2", out var os2) && ReadAt(s, os2.Off, (int)Math.Min(os2.Len, 78)) is { Length: >= 64 } o)
        {
            weight = BinaryPrimitives.ReadUInt16BigEndian(o.AsSpan(4));
            fsType = BinaryPrimitives.ReadUInt16BigEndian(o.AsSpan(8));
            familyClass = BinaryPrimitives.ReadInt16BigEndian(o.AsSpan(30)) >> 8;
            italic = (BinaryPrimitives.ReadUInt16BigEndian(o.AsSpan(62)) & 1) != 0;
        }
        // sFamilyClass 1~5, 7은 세리프 계열(OldStyle·Transitional·Modern·Clarendon·Slab·Freeform). 값이 없으면 이름 규칙으로.
        bool serif = familyClass is >= 1 and <= 5 or 7
            || familyClass == 0 && AppFontCatalog.Classify(new PdfFontInfo(family)).Family == AppFontFamily.Serif
            || familyClass == 0 && local is not null && AppFontCatalog.Classify(new PdfFontInfo(local)).Family == AppFontFamily.Serif;

        FontOutline outline = FontOutline.Unknown;
        if (tables.ContainsKey("glyf")) outline = FontOutline.TrueType;
        else if (tables.ContainsKey("CFF2")) outline = FontOutline.Cff2;
        else if (tables.TryGetValue("CFF ", out var cff))
            outline = IsCidKeyedCff(ReadAt(s, cff.Off, (int)Math.Min(cff.Len, 64 * 1024))) ? FontOutline.CffCid : FontOutline.CffNameKeyed;
        // 글꼴 모음 안의 CFF는 CffCharset이 파일 맨 앞을 표 머리로 보므로 다루지 않는다.
        if (offset != 0 && outline == FontOutline.CffCid) outline = FontOutline.CffNameKeyed;

        var all = names.Where(x => x.Id is 1 or 4 or 6 or 16 && !string.IsNullOrWhiteSpace(x.Text))
            .Select(x => x.Text!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        return new OpenTypeFace(index, family.Trim(), local?.Trim(), AnyOf(6)?.Trim(), all, weight, italic, serif, fsType, outline, tables.ContainsKey("fvar"));
    }

    private static List<(int Id, ushort Lang, string? Text)> ReadNames(byte[] t)
    {
        var list = new List<(int, ushort, string?)>();
        if (t.Length < 6) return list;
        int count = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(2));
        int strOff = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            int r = 6 + i * 12;
            if (r + 12 > t.Length) break;
            int platform = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(r));
            int encoding = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(r + 2));
            ushort lang = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(r + 4));
            int id = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(r + 6));
            int len = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(r + 8));
            int off = strOff + BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(r + 10));
            if (id is not (1 or 4 or 6 or 16) || off + len > t.Length) continue;
            // Windows 유니코드(3,1·3,10)만 읽는다. 맥 이름(1,0)은 영어 이름이 같이 있으므로 건너뛴다.
            if (platform != 3 || encoding is not (1 or 10)) continue;
            list.Add((id, lang, Encoding.BigEndianUnicode.GetString(t, off, len)));
        }
        return list;
    }

    /// <summary>CFF 첫 Top DICT에 ROS(12 30) 연산자가 있으면 CID 방식.</summary>
    private static bool IsCidKeyedCff(byte[]? cff)
    {
        if (cff is null || cff.Length < 4) return false;
        int pos = cff[2];
        if (!SkipIndex(cff, ref pos)) return false;   // Name INDEX
        if (pos + 3 > cff.Length) return false;
        int count = BinaryPrimitives.ReadUInt16BigEndian(cff.AsSpan(pos));
        if (count == 0) return false;
        int offSize = cff[pos + 2];
        int baseOff = pos + 3 + (count + 1) * offSize - 1;
        int start = baseOff + ReadOff(cff, pos + 3, offSize), end = baseOff + ReadOff(cff, pos + 3 + offSize, offSize);
        if (end > cff.Length || start >= end) return false;
        for (int p = start; p < end;)
        {
            int b0 = cff[p];
            if (b0 == 12) { if (p + 1 < end && cff[p + 1] == 30) return true; p += 2; }
            else if (b0 <= 21) p++;
            else if (b0 == 28) p += 3;
            else if (b0 == 29) p += 5;
            else if (b0 == 30) { p++; while (p < end) { int b = cff[p++]; if ((b & 0x0F) == 0x0F || (b >> 4) == 0x0F) break; } }
            else if (b0 >= 247 && b0 <= 254) p += 2;
            else p++;
        }
        return false;
    }

    private static bool SkipIndex(byte[] c, ref int pos)
    {
        if (pos + 2 > c.Length) return false;
        int count = BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(pos));
        if (count == 0) { pos += 2; return true; }
        if (pos + 3 > c.Length) return false;
        int offSize = c[pos + 2];
        int last = pos + 3 + count * offSize;
        if (offSize is < 1 or > 4 || last + offSize > c.Length) return false;
        pos = last + offSize - 1 + ReadOff(c, last, offSize);
        return pos <= c.Length;
    }

    private static int ReadOff(byte[] c, int at, int size)
    {
        int v = 0;
        for (int i = 0; i < size; i++) v = (v << 8) | c[at + i];
        return v;
    }

    private static byte[]? ReadAt(Stream s, long offset, int count)
    {
        if (count <= 0 || offset < 0 || offset + count > s.Length) return null;
        s.Position = offset;
        var buf = new byte[count];
        s.ReadExactly(buf);
        return buf;
    }
}
