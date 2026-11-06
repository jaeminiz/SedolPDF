namespace PdfEditor.Optimize;

/// <summary>
/// 1비트 그림을 JBIG2 일반 영역(무손실, 템플릿 0, 산술 부호화)으로 압축한다. PDF의 /JBIG2Decode 스트림(전역 데이터 없음)으로 그대로 쓴다.
/// JBIG2에서 1은 검정이고 PDF 필터가 뒤집어 주므로 입력도 1 = 검정이다.
/// </summary>
internal static class Jbig2Encoder
{
    // MQ 부호화기 확률 표 (T.88 부록 E): Qe, 다음 상태(MPS), 다음 상태(LPS), MPS 교체 여부
    private static readonly (ushort Qe, byte Nmps, byte Nlps, byte Switch)[] Table =
    [
        (0x5601, 1, 1, 1), (0x3401, 2, 6, 0), (0x1801, 3, 9, 0), (0x0AC1, 4, 12, 0), (0x0521, 5, 29, 0), (0x0221, 38, 33, 0),
        (0x5601, 7, 6, 1), (0x5401, 8, 14, 0), (0x4801, 9, 14, 0), (0x3801, 10, 14, 0), (0x3001, 11, 17, 0), (0x2401, 12, 18, 0),
        (0x1C01, 13, 20, 0), (0x1601, 29, 21, 0), (0x5601, 15, 14, 1), (0x5401, 16, 14, 0), (0x5101, 17, 15, 0), (0x4801, 18, 16, 0),
        (0x3801, 19, 17, 0), (0x3401, 20, 18, 0), (0x3001, 21, 19, 0), (0x2801, 22, 19, 0), (0x2401, 23, 20, 0), (0x2201, 24, 21, 0),
        (0x1C01, 25, 22, 0), (0x1801, 26, 23, 0), (0x1601, 27, 24, 0), (0x1401, 28, 25, 0), (0x1201, 29, 26, 0), (0x1101, 30, 27, 0),
        (0x0AC1, 31, 28, 0), (0x09C1, 32, 29, 0), (0x08A1, 33, 30, 0), (0x0521, 34, 31, 0), (0x0441, 35, 32, 0), (0x02A1, 36, 33, 0),
        (0x0221, 37, 34, 0), (0x0141, 38, 35, 0), (0x0111, 39, 36, 0), (0x0085, 40, 37, 0), (0x0049, 41, 38, 0), (0x0025, 42, 39, 0),
        (0x0015, 43, 40, 0), (0x0009, 44, 41, 0), (0x0005, 45, 42, 0), (0x0001, 45, 43, 0), (0x5601, 46, 46, 0),
    ];

    private sealed class Mq
    {
        private uint _a = 0x8000, _c;
        private int _ct = 12;
        private readonly List<byte> _out = [0];   // 맨 앞 0은 "직전 바이트" 자리(출력하지 않는다)
        private readonly byte[] _state = new byte[65536];
        private readonly byte[] _mps = new byte[65536];

        private byte B { get => _out[^1]; set => _out[^1] = value; }

        public void Encode(int cx, int bit)
        {
            var q = Table[_state[cx]];
            if (bit == _mps[cx])
            {
                _a -= q.Qe;
                if ((_a & 0x8000) == 0)
                {
                    if (_a < q.Qe) _a = q.Qe; else _c += q.Qe;
                    _state[cx] = q.Nmps;
                    Renorm();
                }
                else _c += q.Qe;
            }
            else
            {
                _a -= q.Qe;
                if (_a < q.Qe) _c += q.Qe; else _a = q.Qe;
                if (q.Switch == 1) _mps[cx] ^= 1;
                _state[cx] = q.Nlps;
                Renorm();
            }
        }

        private void Renorm()
        {
            do
            {
                _a <<= 1; _c <<= 1; _ct--;
                if (_ct == 0) ByteOut();
            } while ((_a & 0x8000) == 0);
        }

        private void ByteOut()
        {
            if (B == 0xFF) { _out.Add((byte)(_c >> 20)); _c &= 0xFFFFF; _ct = 7; }
            else if ((_c & 0x8000000) == 0) { _out.Add((byte)(_c >> 19)); _c &= 0x7FFFF; _ct = 8; }
            else
            {
                B++;
                if (B == 0xFF) { _c &= 0x7FFFFFF; _out.Add((byte)(_c >> 20)); _c &= 0xFFFFF; _ct = 7; }
                else { _out.Add((byte)(_c >> 19)); _c &= 0x7FFFF; _ct = 8; }
            }
        }

        public byte[] Finish()
        {
            uint temp = _c + _a;
            _c |= 0xFFFF;
            if (_c >= temp) _c -= 0x8000;
            _c <<= _ct; ByteOut();
            _c <<= _ct; ByteOut();
            if (B != 0xFF) _out.Add(0xFF);
            _out.Add(0xAC);
            return _out.Skip(1).ToArray();
        }
    }

    /// <param name="px">w*h바이트, 1 = 검정, 0 = 흰색.</param>
    public static byte[] EncodePdfStream(byte[] px, int w, int h)
    {
        // 위·좌·우에 0으로 채운 사본(문맥 계산에서 경계 검사를 없앤다)
        int pw = w + 8, ph = h + 2;
        var p = new byte[pw * ph];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(px, y * w, p, (y + 2) * pw + 4, w);

        var mq = new Mq();
        for (int y = 0; y < h; y++)
        {
            int row = (y + 2) * pw + 4;
            for (int x = 0; x < w; x++)
            {
                int b = row + x;
                int cx = p[b - 1] | p[b - 2] << 1 | p[b - 3] << 2 | p[b - 4] << 3
                    | p[b + 3 - pw] << 4 | p[b + 2 - pw] << 5 | p[b + 1 - pw] << 6 | p[b - pw] << 7 | p[b - 1 - pw] << 8 | p[b - 2 - pw] << 9 | p[b - 3 - pw] << 10
                    | p[b + 2 - 2 * pw] << 11 | p[b + 1 - 2 * pw] << 12 | p[b - 2 * pw] << 13 | p[b - 1 - 2 * pw] << 14 | p[b - 2 - 2 * pw] << 15;
                mq.Encode(cx, p[b]);
            }
        }
        byte[] data = mq.Finish();

        using var ms = new MemoryStream();
        void U32(int v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        // 쪽 정보 세그먼트(번호 0, 유형 48)
        U32(0); ms.WriteByte(0x30); ms.WriteByte(0); ms.WriteByte(1); U32(19);
        U32(w); U32(h); U32(0); U32(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
        // 즉시 일반 영역 세그먼트(번호 1, 유형 38)
        U32(1); ms.WriteByte(0x26); ms.WriteByte(0); ms.WriteByte(1); U32(26 + data.Length);
        U32(w); U32(h); U32(0); U32(0); ms.WriteByte(0);
        ms.WriteByte(0);   // 템플릿 0, MMR 아님, TPGDON 끔
        ms.Write([3, 0xFF, 0xFD, 0xFF, 2, 0xFE, 0xFE, 0xFE]);   // 적응 화소 (3,-1) (-3,-1) (2,-2) (-2,-2)
        ms.Write(data);
        return ms.ToArray();
    }
}
