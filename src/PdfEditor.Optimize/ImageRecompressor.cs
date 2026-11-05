using System.Runtime.InteropServices;
using PdfEditor.ContentStream;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using SkiaSharp;

namespace PdfEditor.Optimize;

/// <param name="Images">처리 후보로 찾은 이미지 XObject 수(같은 객체는 한 번만 센다).</param>
/// <param name="Replaced">더 작게 다시 만든 수.</param>
/// <param name="Skipped">건드리지 않은 수(지원하지 않는 형식이거나 다시 만든 결과가 더 컸다).</param>
public sealed record RecompressStats(int Images, int Replaced, int Skipped, long BytesBefore, long BytesAfter);

/// <summary>
/// 이미지 재압축(용량 줄이기 3단계). 쪽의 이미지 XObject마다 쪽에 그려지는 크기로 유효 dpi를 구해, 단계의 최대 dpi를 넘으면 줄이고
/// JPEG(DCTDecode)로 다시 인코딩한다. 확대는 하지 않고, 결과가 원래 스트림보다 크면 원본을 둔다. 문서를 제자리에서 바꾼다(저장은 호출자).
/// 배치를 알 수 없는 이미지(양식 XObject 안 등)는 크기를 줄이지 않고 화질만 낮춘다.
/// 다루는 형식: DeviceRGB·DeviceGray 8비트의 DCTDecode, FlateDecode(예측기 없음), 필터 없음. 그 밖(CMYK·ICC·Indexed·1비트·마스크·JPX·CCITT·JBIG2)은 그대로 둔다.
/// </summary>
public static class ImageRecompressor
{
    public static RecompressStats Recompress(PdfSharp.Pdf.PdfDocument doc, LadderLevel level, CancellationToken ct)
    {
        var candidates = new Dictionary<int, Candidate>();
        foreach (PdfPage page in doc.Pages)
        {
            ct.ThrowIfCancellationRequested();
            var xo = page.Resources?.Elements.GetDictionary("/XObject");
            if (xo is null) continue;
            var byName = new Dictionary<string, Candidate>();
            foreach (var key in xo.Elements.Keys)
            {
                if (xo.Elements.GetDictionary(key) is not { } d || NameOf(d.Elements, "/Subtype") != "/Image") continue;
                int num = d.Reference?.ObjectNumber ?? 0;
                if (num == 0) continue;
                if (!candidates.TryGetValue(num, out var c)) candidates[num] = c = new Candidate(d);
                byName[key] = c;
            }
            if (byName.Count == 0) continue;
            var loaded = PageContent.Load(page);
            if (loaded.Data is not null) ObservePlacements(ContentLexer.Parse(loaded.Data), byName);
        }

        long before = 0, after = 0;
        int replaced = 0, skipped = 0;
        foreach (var c in candidates.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (c.Dict.Stream is not { } stream) { skipped++; continue; }
            long oldLen = stream.Value.Length;
            before += oldLen;
            var result = TryRecompress(c, stream, level);
            if (result is null || result.Jpeg.Length >= oldLen) { skipped++; after += oldLen; continue; }
            var e = c.Dict.Elements;
            stream.Value = result.Jpeg;
            e.SetName("/Filter", "/DCTDecode");
            e.SetInteger("/Width", result.Width);
            e.SetInteger("/Height", result.Height);
            if (!result.KeepColorSpace) e.SetName("/ColorSpace", result.Gray ? "/DeviceGray" : "/DeviceRGB");   // ICC 등 프로필 배열은 그대로 둔다
            e.SetInteger("/BitsPerComponent", 8);
            e.Remove("/DecodeParms");
            replaced++;
            after += result.Jpeg.Length;
        }
        return new RecompressStats(candidates.Count, replaced, skipped, before, after);
    }

    private sealed class Candidate(PdfDictionary dict)
    {
        public PdfDictionary Dict { get; } = dict;
        /// <summary>쪽에서 관찰한 가장 높은 유효 dpi. 0이면 배치를 모른다.</summary>
        public double MaxDpi { get; set; }
    }

    private sealed record Encoded(byte[] Jpeg, int Width, int Height, bool Gray, bool KeepColorSpace = false);

    /// <summary>q/Q/cm으로 변환 행렬을 쌓아 Do 시점의 행렬에서 그려지는 크기(pt)를 구하고, 그 크기로 유효 dpi를 기록한다.</summary>
    private static void ObservePlacements(ParsedContent parsed, Dictionary<string, Candidate> byName)
    {
        var stack = new Stack<Mat>();
        var ctm = Mat.Identity;
        foreach (var op in parsed.Ops)
        {
            switch (op.Name)
            {
                case "q": stack.Push(ctm); break;
                case "Q": if (stack.Count > 0) ctm = stack.Pop(); break;
                case "cm":
                    if (op.Operands.Count == 6 && op.Operands.All(o => o.Kind == OperandKind.Number))
                        ctm = new Mat(op.Operands[0].Number, op.Operands[1].Number, op.Operands[2].Number, op.Operands[3].Number, op.Operands[4].Number, op.Operands[5].Number).Then(ctm);
                    break;
                case "Do":
                    if (op.Operands.Count == 1 && op.Operands[0].Kind == OperandKind.Name && byName.TryGetValue("/" + op.Operands[0].Text, out var c))
                    {
                        double wPt = Math.Sqrt(ctm.A * ctm.A + ctm.B * ctm.B), hPt = Math.Sqrt(ctm.C * ctm.C + ctm.D * ctm.D);
                        int pw = c.Dict.Elements.GetInteger("/Width"), ph = c.Dict.Elements.GetInteger("/Height");
                        if (wPt > 0 && hPt > 0 && pw > 0 && ph > 0)
                            c.MaxDpi = Math.Max(c.MaxDpi, Math.Max(pw / (wPt / 72.0), ph / (hPt / 72.0)));
                    }
                    break;
            }
        }
    }

    private static PdfItem? Resolved(PdfDictionary.DictionaryElements e, string key)
        => e.ContainsKey(key) ? (e[key] is PdfReference r ? r.Value : e[key]) : null;

    /// <summary>이름 값이면 그 문자열, 없거나 이름이 아니면(예: /ColorSpace가 [/ICCBased …] 배열) null. GetName은 이름이 아니면 예외를 던지므로 쓰지 않는다.</summary>
    private static string? NameOf(PdfDictionary.DictionaryElements e, string key) => Resolved(e, key) is PdfName n ? n.Value : null;

    private static bool IsTrue(PdfDictionary.DictionaryElements e, string key) => Resolved(e, key) is PdfBoolean { Value: true };

    private static string? FilterOf(PdfDictionary d)
    {
        if (!d.Elements.ContainsKey("/Filter")) return "";
        return Resolved(d.Elements, "/Filter") switch
        {
            PdfName n => n.Value,
            PdfArray { Elements.Count: 1 } a when a.Elements[0] is PdfName n1 => n1.Value,
            _ => null,
        };
    }

    private static Encoded? TryRecompress(Candidate c, PdfDictionary.PdfStream stream, LadderLevel level)
    {
        var d = c.Dict;
        var e = d.Elements;
        if (IsTrue(e, "/ImageMask") || e.ContainsKey("/Mask") || e.ContainsKey("/Decode")) return null;
        int w = e.GetInteger("/Width"), h = e.GetInteger("/Height");
        if (w <= 0 || h <= 0 || e.GetInteger("/BitsPerComponent") != 8) return null;
        var (comps, keepColorSpace) = ColorModel(Resolved(e, "/ColorSpace"));
        if (comps == 0) return null;
        bool gray = comps == 1;
        string? filter = FilterOf(d);

        using var bmp = filter switch
        {
            "/DCTDecode" => DecodeJpeg(stream.Value, w, h),
            "/FlateDecode" or "" when PredictorOk(d) => DecodeRaw(stream.UnfilteredValue, w, h, gray),
            _ => null,
        };
        if (bmp is null) return null;

        int tw = w, th = h;
        if (c.MaxDpi > level.MaxDpi)
        {
            double f = level.MaxDpi / c.MaxDpi;
            tw = Math.Max(1, (int)Math.Round(w * f));
            th = Math.Max(1, (int)Math.Round(h * f));
        }
        SKBitmap? resized = null;
        try
        {
            var src = bmp;
            if (tw != w || th != h)
            {
                resized = bmp.Resize(new SKImageInfo(tw, th, SKColorType.Rgba8888, SKAlphaType.Opaque), new SKSamplingOptions(SKCubicResampler.Mitchell));
                if (resized is null) return null;
                src = resized;
            }
            var enc = Encode(src, gray, level.JpegQuality);
            if (enc is null || (keepColorSpace && enc.Gray != gray)) return null;   // 프로필을 그대로 두려면 성분 수가 같아야 한다
            return enc with { KeepColorSpace = keepColorSpace };
        }
        finally { resized?.Dispose(); }
    }

    /// <summary>
    /// 색 공간의 성분 수와 "원래 배열을 그대로 둘지". DeviceRGB/DeviceGray는 (3|1, false)이고 ICCBased(/N 1·3)·CalRGB·CalGray는 프로필을 그대로 두므로 (n, true).
    /// 그 밖(CMYK·Indexed·Separation·Lab 등)은 (0, false) — 건드리지 않는다.
    /// </summary>
    private static (int Comps, bool Keep) ColorModel(PdfItem? cs)
    {
        switch (cs)
        {
            case PdfName { Value: "/DeviceRGB" }: return (3, false);
            case PdfName { Value: "/DeviceGray" }: return (1, false);
            case PdfArray { Elements.Count: >= 1 } a when a.Elements[0] is PdfName family:
                switch (family.Value)
                {
                    case "/CalRGB": return (3, true);
                    case "/CalGray": return (1, true);
                    case "/ICCBased" when a.Elements.Count >= 2:
                        var profile = a.Elements[1] is PdfReference r ? r.Value : a.Elements[1];
                        if (profile is PdfDictionary pd && pd.Elements.ContainsKey("/N"))
                        {
                            int n = pd.Elements.GetInteger("/N");
                            if (n is 1 or 3) return (n, true);
                        }
                        break;
                }
                break;
        }
        return (0, false);
    }

    /// <summary>Flate 예측기: 없거나 1(없음)이거나 PNG 계열(10 이상)이면 PDFsharp이 풀어 주고(길이는 호출자가 다시 확인한다), TIFF(2) 등은 풀지 못하므로 건드리지 않는다.</summary>
    private static bool PredictorOk(PdfDictionary d)
    {
        var p = Resolved(d.Elements, "/DecodeParms");
        if (p is PdfArray { Elements.Count: 1 } a) p = a.Elements[0] is PdfReference r ? r.Value : a.Elements[0];
        if (p is null) return true;
        if (p is not PdfDictionary dp) return false;
        int predictor = dp.Elements.ContainsKey("/Predictor") ? dp.Elements.GetInteger("/Predictor") : 1;
        return predictor == 1 || predictor >= 10;
    }

    private static SKBitmap? DecodeJpeg(byte[] jpeg, int w, int h)
    {
        var decoded = SKBitmap.Decode(jpeg);
        if (decoded is null) return null;
        if (decoded.Width != w || decoded.Height != h) { decoded.Dispose(); return null; }
        if (decoded.ColorType == SKColorType.Rgba8888 && decoded.AlphaType == SKAlphaType.Opaque) return decoded;
        var rgba = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bool ok = decoded.CopyTo(rgba, SKColorType.Rgba8888);
        decoded.Dispose();
        if (ok) return rgba;
        rgba.Dispose();
        return null;
    }

    private static SKBitmap? DecodeRaw(byte[] raw, int w, int h, bool gray)
    {
        int comps = gray ? 1 : 3;
        if (raw.Length != (long)w * h * comps) return null;
        var rgba = new byte[w * h * 4];
        for (int i = 0, o = 0; i < w * h; i++, o += 4)
        {
            if (gray) { byte g = raw[i]; rgba[o] = rgba[o + 1] = rgba[o + 2] = g; }
            else { rgba[o] = raw[i * 3]; rgba[o + 1] = raw[i * 3 + 1]; rgba[o + 2] = raw[i * 3 + 2]; }
            rgba[o + 3] = 255;
        }
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        if (bmp.RowBytes != w * 4) { bmp.Dispose(); return null; }
        Marshal.Copy(rgba, 0, bmp.GetPixels(), rgba.Length);
        return bmp;
    }

    private static Encoded? Encode(SKBitmap rgba, bool gray, int quality)
    {
        if (gray)
        {
            using var g = new SKBitmap(new SKImageInfo(rgba.Width, rgba.Height, SKColorType.Gray8, SKAlphaType.Opaque));
            if (rgba.CopyTo(g, SKColorType.Gray8))
            {
                using var gi = SKImage.FromBitmap(g);
                using var gd = gi.Encode(SKEncodedImageFormat.Jpeg, quality);
                if (gd is not null) return new Encoded(gd.ToArray(), rgba.Width, rgba.Height, true);
            }
        }
        using var img = SKImage.FromBitmap(rgba);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data is null ? null : new Encoded(data.ToArray(), rgba.Width, rgba.Height, false);
    }
}
