using System.Buffers.Binary;

namespace S1.FreeTextKorean;

/// <summary>
/// OpenType('CFF ' 테이블) 폰트에서 글리프 ID → CID 대응표를 읽는다.
/// CID 키 방식 CFF는 PDF 안에서 글리프 ID가 아니라 CID로 글자를 가리켜야 하기 때문이다.
/// 이름 키 방식(ROS 없음)이면 null을 돌려주고, 그때는 CID = 글리프 ID이다.
/// </summary>
public static class CffCharset
{
    /// <summary>OpenType 래퍼를 벗기고 순수 CFF 테이블만 꺼낸다 (PDF에는 FontFile3 /CIDFontType0C로 넣는다).</summary>
    public static byte[] ExtractCff(byte[] otf)
    {
        var (off, len) = FindCff(otf);
        return otf.AsSpan(off, len).ToArray();
    }

    private static (int off, int len) FindCff(byte[] otf)
    {
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(otf.AsSpan(4));
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            if (otf[rec] == (byte)'C' && otf[rec + 1] == (byte)'F' && otf[rec + 2] == (byte)'F' && otf[rec + 3] == (byte)' ')
                return ((int)BinaryPrimitives.ReadUInt32BigEndian(otf.AsSpan(rec + 8)), (int)BinaryPrimitives.ReadUInt32BigEndian(otf.AsSpan(rec + 12)));
        }
        throw new InvalidOperationException("CFF 테이블 없음");
    }

    public static int[]? GetCidByGid(byte[] otf)
    {
        int cffOff = -1, cffLen = 0;
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(otf.AsSpan(4));
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            if (otf[rec] == 'C' && otf[rec + 1] == 'F' && otf[rec + 2] == 'F' && otf[rec + 3] == ' ')
            {
                cffOff = (int)BinaryPrimitives.ReadUInt32BigEndian(otf.AsSpan(rec + 8));
                cffLen = (int)BinaryPrimitives.ReadUInt32BigEndian(otf.AsSpan(rec + 12));
            }
        }
        if (cffOff < 0) return null;
        var cff = otf.AsSpan(cffOff, cffLen).ToArray();

        int pos = cff[2];                         // hdrSize
        pos = SkipIndex(cff, pos);                // Name INDEX
        var (topStart, topEnd) = FirstIndexItem(cff, pos);

        bool isCid = false;
        int charsetOff = 0, charStringsOff = 0;
        var operands = new List<double>();
        for (int p = topStart; p < topEnd;)
        {
            int b0 = cff[p];
            if (b0 <= 21)
            {
                int op = b0;
                p++;
                if (b0 == 12) { op = 1200 + cff[p]; p++; }
                if (op == 1230) isCid = true;
                else if (op == 15) charsetOff = (int)operands[^1];
                else if (op == 17) charStringsOff = (int)operands[^1];
                operands.Clear();
            }
            else if (b0 == 28) { operands.Add(BinaryPrimitives.ReadInt16BigEndian(cff.AsSpan(p + 1))); p += 3; }
            else if (b0 == 29) { operands.Add(BinaryPrimitives.ReadInt32BigEndian(cff.AsSpan(p + 1))); p += 5; }
            else if (b0 == 30)
            {
                p++;
                while (true) { int b = cff[p++]; if ((b & 0x0F) == 0x0F || (b >> 4) == 0x0F) break; }
                operands.Add(0);
            }
            else if (b0 >= 32 && b0 <= 246) { operands.Add(b0 - 139); p++; }
            else if (b0 >= 247 && b0 <= 250) { operands.Add((b0 - 247) * 256 + cff[p + 1] + 108); p += 2; }
            else if (b0 >= 251 && b0 <= 254) { operands.Add(-(b0 - 251) * 256 - cff[p + 1] - 108); p += 2; }
            else p++;
        }
        if (!isCid || charsetOff <= 2) return null;

        int nGlyphs = BinaryPrimitives.ReadUInt16BigEndian(cff.AsSpan(charStringsOff));
        var cids = new int[nGlyphs];
        int q = charsetOff;
        int format = cff[q++];
        int g = 1;
        if (format == 0)
        {
            for (; g < nGlyphs; g++, q += 2) cids[g] = BinaryPrimitives.ReadUInt16BigEndian(cff.AsSpan(q));
        }
        else
        {
            while (g < nGlyphs)
            {
                int first = BinaryPrimitives.ReadUInt16BigEndian(cff.AsSpan(q));
                q += 2;
                int nLeft;
                if (format == 1) nLeft = cff[q++];
                else { nLeft = BinaryPrimitives.ReadUInt16BigEndian(cff.AsSpan(q)); q += 2; }
                for (int k = 0; k <= nLeft && g < nGlyphs; k++) cids[g++] = first + k;
            }
        }
        return cids;
    }

    private static int SkipIndex(byte[] d, int pos)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos));
        if (count == 0) return pos + 2;
        int offSize = d[pos + 2];
        int last = ReadOff(d, pos + 3 + count * offSize, offSize);
        return pos + 3 + (count + 1) * offSize + last - 1;
    }

    private static (int start, int end) FirstIndexItem(byte[] d, int pos)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos));
        int offSize = d[pos + 2];
        int dataStart = pos + 3 + (count + 1) * offSize - 1;
        int o0 = ReadOff(d, pos + 3, offSize);
        int o1 = ReadOff(d, pos + 3 + offSize, offSize);
        return (dataStart + o0, dataStart + o1);
    }

    private static int ReadOff(byte[] d, int p, int size)
    {
        int v = 0;
        for (int i = 0; i < size; i++) v = (v << 8) | d[p + i];
        return v;
    }
}
