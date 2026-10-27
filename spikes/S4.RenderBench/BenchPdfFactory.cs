using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace S4.RenderBench;

/// <summary>벤치마크용 PDF를 직접 써서 만든다 (표준 폰트 Helvetica만 사용, 외부 라이브러리 없음).</summary>
public static class BenchPdfFactory
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>(a) A1 1쪽, 선 20만 개 + 텍스트 5천 개 (도면 모사)</summary>
    public static void CreateDrawing(string path, int lines = 200_000, int texts = 5_000, bool paragraph = false)
    {
        const double W = 1683.78, H = 2383.94;
        var rnd = new Random(1);
        var sb = new StringBuilder(lines * 32);
        sb.Append("0.3 w\n");
        for (int i = 0; i < lines; i++)
        {
            double x = rnd.NextDouble() * (W - 40), y = rnd.NextDouble() * (H - 40);
            double dx = (rnd.NextDouble() - 0.5) * 40, dy = (rnd.NextDouble() - 0.5) * 40;
            if (paragraph && x < 600 && y > 2150 && y < 2360) continue;   // 문단 자리는 선도 비워 둔다
            sb.Append(Inv, $"{x:0.##} {y:0.##} m {x + dx:0.##} {y + dy:0.##} l S\n");
        }
        for (int i = 0; i < texts; i++)
        {
            double x = rnd.NextDouble() * (W - 80), y = rnd.NextDouble() * (H - 20);
            if (paragraph && x < 560 && y > 2180 && y < 2340) continue;   // 문단 자리는 비워 둔다
            sb.Append(Inv, $"BT /F1 8 Tf {x:0.##} {y:0.##} Td (DWG-{i:D5} REV A) Tj ET\n");
        }
        if (paragraph)   // M5 Task 10: 한쪽 구석(왼쪽 위)에 6줄 주기 문단 (Helvetica 12 pt, 줄 간격 15 pt)
        {
            string[] note =
            [
                "General note: all piping shall be installed according",
                "to the approved drawings and the applicable class rules",
                "before the pressure test is carried out by the yard and",
                "the surveyor. Any deviation from the drawings must be",
                "reported to the design office in writing and approved",
                "before the work continues on board the vessel.",
            ];
            for (int i = 0; i < note.Length; i++)
                sb.Append(Inv, $"BT /F1 12 Tf 60 {2300 - i * 15} Td ({note[i]}) Tj ET\n");
        }
        var w = new MiniPdf();
        int font = w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        int content = w.AddStream("", Encoding.ASCII.GetBytes(sb.ToString()), flate: true);
        int pages = w.Reserve();
        int page = w.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 {W.ToString(Inv)} {H.ToString(Inv)}] /Contents {content} 0 R /Resources << /Font << /F1 {font} 0 R >> >> >>");
        w.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        int cat = w.Add($"<< /Type /Catalog /Pages {pages} 0 R >>");
        w.Save(path, cat);
    }

    /// <summary>(b) A4 300쪽, 쪽마다 텍스트 2천 자 + 이미지 1개 (이미지 데이터는 모든 쪽이 공유)</summary>
    public static void CreateManyPages(string path, int pageCount = 300)
    {
        var w = new MiniPdf();
        int font = w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var img = new byte[256 * 256 * 3];
        var rnd = new Random(2);
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                int i = (y * 256 + x) * 3;
                img[i] = (byte)x; img[i + 1] = (byte)y; img[i + 2] = (byte)(rnd.Next(64) + 128);
            }
        int image = w.AddStream("/Type /XObject /Subtype /Image /Width 256 /Height 256 /ColorSpace /DeviceRGB /BitsPerComponent 8", img, flate: true);
        int pages = w.Reserve();
        var kids = new List<int>();
        for (int p = 0; p < pageCount; p++)
        {
            var sb = new StringBuilder();
            sb.Append("BT /F1 10 Tf 14 TL 50 800 Td\n");
            for (int l = 0; l < 40; l++)   // 40줄 x 50자 = 2000자
                sb.Append(Inv, $"({(p * 40 + l):D6} The quick brown fox jumps over the lazy dog 0123) '\n");
            sb.Append("ET\nq 200 0 0 200 350 100 cm /Im1 Do Q\n");
            int c = w.AddStream("", Encoding.ASCII.GetBytes(sb.ToString()), flate: true);
            kids.Add(w.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 595 842] /Contents {c} 0 R /Resources << /Font << /F1 {font} 0 R >> /XObject << /Im1 {image} 0 R >> >> >>"));
        }
        w.Set(pages, $"<< /Type /Pages /Kids [{string.Join(' ', kids.Select(k => k + " 0 R"))}] /Count {pageCount} >>");
        int cat = w.Add($"<< /Type /Catalog /Pages {pages} 0 R >>");
        w.Save(path, cat);
    }

    private sealed class MiniPdf
    {
        private readonly List<byte[]?> _objs = new();

        public int Reserve() { _objs.Add(null); return _objs.Count; }
        public void Set(int id, string body) => _objs[id - 1] = Encoding.ASCII.GetBytes(body);
        public int Add(string body) { _objs.Add(Encoding.ASCII.GetBytes(body)); return _objs.Count; }

        public int AddStream(string dictExtra, byte[] data, bool flate)
        {
            if (flate)
            {
                using var ms = new MemoryStream();
                using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true)) z.Write(data);
                data = ms.ToArray();
                dictExtra += " /Filter /FlateDecode";
            }
            var head = Encoding.ASCII.GetBytes($"<< {dictExtra} /Length {data.Length} >>\nstream\n");
            var tail = Encoding.ASCII.GetBytes("\nendstream");
            _objs.Add(head.Concat(data).Concat(tail).ToArray());
            return _objs.Count;
        }

        public void Save(string path, int root)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var fs = new FileStream(path, FileMode.Create);
            void W(string s) => fs.Write(Encoding.ASCII.GetBytes(s));
            W("%PDF-1.7\n");
            var offsets = new long[_objs.Count];
            for (int i = 0; i < _objs.Count; i++)
            {
                offsets[i] = fs.Position;
                W($"{i + 1} 0 obj\n");
                fs.Write(_objs[i] ?? throw new InvalidOperationException($"객체 {i + 1} 비어 있음"));
                W("\nendobj\n");
            }
            long xref = fs.Position;
            W($"xref\n0 {_objs.Count + 1}\n0000000000 65535 f \n");
            foreach (var o in offsets) W($"{o:D10} 00000 n \n");
            W($"trailer\n<< /Size {_objs.Count + 1} /Root {root} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        }
    }
}
