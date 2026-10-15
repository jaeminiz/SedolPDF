using System.Runtime.InteropServices;
using HarfBuzzSharp;
using Pdf.Native;
using Pdf.Native.Interop;

namespace S3.TextReplace;

internal static unsafe partial class TextNative
{
    private const string Lib = "pdfium";
    public const int FontTrueType = 2;
    public const int ObjText = 1;

    [LibraryImport(Lib)] public static partial IntPtr FPDFText_LoadFont(IntPtr doc, byte* data, uint size, int fontType, int cid);
    [LibraryImport(Lib)] public static partial void FPDFFont_Close(IntPtr font);
    [LibraryImport(Lib)] public static partial IntPtr FPDFPageObj_CreateTextObj(IntPtr doc, IntPtr font, float size);
    [LibraryImport(Lib)] public static partial int FPDFText_SetText(IntPtr textObj, ushort* text);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_Transform(IntPtr obj, double a, double b, double c, double d, double e, double f);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetMatrix(IntPtr obj, FsMatrix* m);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetMatrix(IntPtr obj, FsMatrix* m);
    [LibraryImport(Lib)] public static partial void FPDFPage_InsertObject(IntPtr page, IntPtr obj);
    [LibraryImport(Lib)] public static partial int FPDFPage_RemoveObject(IntPtr page, IntPtr obj);
    [LibraryImport(Lib)] public static partial void FPDFPageObj_Destroy(IntPtr obj);
    [LibraryImport(Lib)] public static partial int FPDFPage_GenerateContent(IntPtr page);
    [LibraryImport(Lib)] public static partial int FPDFPage_CountObjects(IntPtr page);
    [LibraryImport(Lib)] public static partial int FPDFFormObj_CountObjects(IntPtr form);
    [LibraryImport(Lib)] public static partial IntPtr FPDFFormObj_GetObject(IntPtr form, nuint index);
    [LibraryImport(Lib)] public static partial IntPtr FPDFPage_GetObject(IntPtr page, int index);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetType(IntPtr obj);
    [LibraryImport(Lib)] public static partial IntPtr FPDFTextObj_GetFont(IntPtr textObj);
    [LibraryImport(Lib)] public static partial int FPDFTextObj_GetFontSize(IntPtr textObj, float* size);
    [LibraryImport(Lib)] public static partial int FPDFFont_GetFontData(IntPtr font, byte* buffer, nuint len, nuint* outLen);
    [LibraryImport(Lib)] public static partial IntPtr FPDFText_LoadPage(IntPtr page);
    [LibraryImport(Lib)] public static partial void FPDFText_ClosePage(IntPtr textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_CountChars(IntPtr textPage);
    [LibraryImport(Lib)] public static partial nuint FPDFTextObj_GetText(IntPtr textObj, IntPtr textPage, ushort* buffer, nuint length);
    [LibraryImport(Lib)] public static partial int FPDFText_GetText(IntPtr textPage, int start, int count, ushort* result);
}

internal static class Fonts
{
    public static byte[] LoadNoto()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var f = Path.Combine(dir.FullName, "assets", "fonts", "NotoSansKR-Regular.otf");
            if (File.Exists(f)) return File.ReadAllBytes(f);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("NotoSansKR-Regular.otf");
    }
}

internal static class TestOut
{
    public static string Dir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PdfEditor.sln"))) dir = dir.Parent;
        var o = Path.Combine(dir!.FullName, "spikes", "out");
        Directory.CreateDirectory(o);
        return o;
    }
}

/// <summary>S3 실험: PDFium의 텍스트 객체 API로 줄을 교체하고, 폰트에 없는 글자는 앱 폰트로 대체한다.</summary>
internal static unsafe class TextReplacer
{
    private static ushort[] Wide(string s) => s.Select(c => (ushort)c).Append((ushort)0).ToArray();

    private static IntPtr NewTextObject(PdfDocument doc, byte[] fontBytes, string text, float size)
    {
        IntPtr font;
        fixed (byte* p = fontBytes)
            font = TextNative.FPDFText_LoadFont(doc.Handle, p, (uint)fontBytes.Length, TextNative.FontTrueType, 1);
        if (font == IntPtr.Zero) throw new PdfiumException(0, "FPDFText_LoadFont 실패");
        var obj = TextNative.FPDFPageObj_CreateTextObj(doc.Handle, font, size);
        if (obj == IntPtr.Zero) throw new PdfiumException(0, "텍스트 객체 생성 실패");
        var w = Wide(text);
        fixed (ushort* tp = w)
            if (TextNative.FPDFText_SetText(obj, tp) == 0) throw new PdfiumException(0, "FPDFText_SetText 실패");
        TextNative.FPDFFont_Close(font);
        return obj;
    }

    public static void CreateText(PdfDocument doc, PdfPage page, byte[] fontBytes, string text, float size, double x, double y)
    {
        var obj = NewTextObject(doc, fontBytes, text, size);
        TextNative.FPDFPageObj_Transform(obj, 1, 0, 0, 1, x, y);
        TextNative.FPDFPage_InsertObject(page.Handle, obj);
        TextNative.FPDFPage_GenerateContent(page.Handle);
    }

    public static IntPtr FindFirstTextObject(PdfPage page)
    {
        int n = TextNative.FPDFPage_CountObjects(page.Handle);
        for (int i = 0; i < n; i++)
        {
            var o = TextNative.FPDFPage_GetObject(page.Handle, i);
            if (TextNative.FPDFPageObj_GetType(o) == TextNative.ObjText) return o;
        }
        throw new InvalidOperationException("텍스트 객체 없음");
    }

    /// <summary>텍스트 객체 자신의 글자 (textPage는 FPDFText_LoadPage 결과)</summary>
    public static string ObjectText(IntPtr obj, IntPtr textPage)
    {
        nuint bytes = TextNative.FPDFTextObj_GetText(obj, textPage, null, 0);
        if (bytes < 2) return "";
        var buf = new ushort[bytes / 2 + 1];
        fixed (ushort* p = buf) TextNative.FPDFTextObj_GetText(obj, textPage, p, bytes);
        return new string(buf.Select(c => (char)c).TakeWhile(c => c != (char)0).ToArray());
    }

    public static IntPtr LoadTextPage(PdfPage page) => TextNative.FPDFText_LoadPage(page.Handle);
    public static void CloseTextPage(IntPtr tp) => TextNative.FPDFText_ClosePage(tp);

    public static int ObjectCount(PdfPage page) => TextNative.FPDFPage_CountObjects(page.Handle);

    /// <summary>index번째 객체가 텍스트 객체면 핸들, 아니면 0</summary>
    public static IntPtr TextObjectAt(PdfPage page, int index)
    {
        var o = TextNative.FPDFPage_GetObject(page.Handle, index);
        return TextNative.FPDFPageObj_GetType(o) == TextNative.ObjText ? o : IntPtr.Zero;
    }

    public static int TextObjectCount(PdfPage page)
    {
        int n = TextNative.FPDFPage_CountObjects(page.Handle), c = 0;
        for (int i = 0; i < n; i++)
            if (TextNative.FPDFPageObj_GetType(TextNative.FPDFPage_GetObject(page.Handle, i)) == TextNative.ObjText) c++;
        return c;
    }

    public static bool SetText(PdfPage page, IntPtr obj, string text)
    {
        var w = Wide(text);
        int ok;
        fixed (ushort* tp = w) ok = TextNative.FPDFText_SetText(obj, tp);
        TextNative.FPDFPage_GenerateContent(page.Handle);
        return ok != 0;
    }

    /// <summary>텍스트 객체가 쓰는 원본(임베디드) 폰트에 없는 코드포인트를 돌려준다.</summary>
    public static int[] MissingGlyphs(IntPtr textObj, string text)
    {
        var font = TextNative.FPDFTextObj_GetFont(textObj);
        nuint len = 0;
        TextNative.FPDFFont_GetFontData(font, null, 0, &len);
        if (len == 0) throw new InvalidOperationException("임베디드 폰트 데이터를 읽을 수 없음");
        var data = new byte[len];
        fixed (byte* p = data) TextNative.FPDFFont_GetFontData(font, p, len, &len);

        using var blob = Blob.FromStream(new MemoryStream(data));
        using var face = new Face(blob, 0);
        using var f = new HarfBuzzSharp.Font(face);
        return text.EnumerateRunes().Select(r => r.Value).Distinct()
            .Where(cp => !f.TryGetGlyph((uint)cp, out _)).ToArray();
    }

    public static FsMatrix GetMatrix(IntPtr obj)
    {
        FsMatrix m;
        TextNative.FPDFPageObj_GetMatrix(obj, &m);
        return m;
    }

    /// <summary>원래 객체를 지우고, 같은 위치·크기·행렬로 앱 폰트 텍스트 객체를 만든다.</summary>
    public static IntPtr ReplaceWithFallback(PdfDocument doc, PdfPage page, IntPtr oldObj, string text, byte[] fallbackFont)
    {
        float size;
        TextNative.FPDFTextObj_GetFontSize(oldObj, &size);
        var m = GetMatrix(oldObj);
        TextNative.FPDFPage_RemoveObject(page.Handle, oldObj);
        TextNative.FPDFPageObj_Destroy(oldObj);

        var obj = NewTextObject(doc, fallbackFont, text, size);
        TextNative.FPDFPageObj_SetMatrix(obj, &m);
        TextNative.FPDFPage_InsertObject(page.Handle, obj);
        TextNative.FPDFPage_GenerateContent(page.Handle);
        return obj;
    }

    public static string ExtractText(PdfPage page)
    {
        var tp = TextNative.FPDFText_LoadPage(page.Handle);
        try
        {
            int n = TextNative.FPDFText_CountChars(tp);
            var buf = new ushort[n + 1];
            fixed (ushort* p = buf) TextNative.FPDFText_GetText(tp, 0, n, p);
            return new string(buf.Select(c => (char)c).TakeWhile(c => c != '\0').ToArray()).Trim();
        }
        finally { TextNative.FPDFText_ClosePage(tp); }
    }
}
