using System.Runtime.InteropServices;
using HarfBuzzSharp;

namespace S2.FontSubset;

/// <summary>
/// HarfBuzzSharp의 관리 API에는 hb-subset이 없어서, 같은 패키지의 네이티브 libHarfBuzzSharp.dll이
/// export하는 hb_subset_* 함수를 직접 P/Invoke한다.
/// </summary>
public static unsafe partial class FontSubsetter
{
    private const string Lib = "libHarfBuzzSharp";

    [LibraryImport(Lib)] private static partial IntPtr hb_subset_input_create_or_fail();
    [LibraryImport(Lib)] private static partial IntPtr hb_subset_input_unicode_set(IntPtr input);
    [LibraryImport(Lib)] private static partial void hb_subset_input_destroy(IntPtr input);
    [LibraryImport(Lib)] private static partial void hb_set_add(IntPtr set, uint codepoint);
    [LibraryImport(Lib)] private static partial IntPtr hb_subset_or_fail(IntPtr source, IntPtr input);
    [LibraryImport(Lib)] private static partial IntPtr hb_face_reference_blob(IntPtr face);
    [LibraryImport(Lib)] private static partial byte* hb_blob_get_data(IntPtr blob, uint* length);
    [LibraryImport(Lib)] private static partial void hb_blob_destroy(IntPtr blob);
    [LibraryImport(Lib)] private static partial void hb_face_destroy(IntPtr face);

    /// <summary>요청한 코드포인트의 글리프만 남긴 폰트(OpenType) 바이트를 돌려준다.</summary>
    public static byte[] Subset(byte[] fontData, IReadOnlyCollection<int> codepoints)
    {
        using var blob = Blob.FromStream(new MemoryStream(fontData));
        using var face = new Face(blob, 0);

        IntPtr input = hb_subset_input_create_or_fail();
        if (input == IntPtr.Zero) throw new InvalidOperationException("hb_subset_input_create_or_fail 실패");
        IntPtr resultFace = IntPtr.Zero, resultBlob = IntPtr.Zero;
        try
        {
            var set = hb_subset_input_unicode_set(input);
            foreach (var cp in codepoints) hb_set_add(set, (uint)cp);

            resultFace = hb_subset_or_fail(face.Handle, input);
            if (resultFace == IntPtr.Zero) throw new InvalidOperationException("hb_subset_or_fail 실패");

            resultBlob = hb_face_reference_blob(resultFace);
            uint len = 0;
            byte* data = hb_blob_get_data(resultBlob, &len);
            return new ReadOnlySpan<byte>(data, (int)len).ToArray();
        }
        finally
        {
            if (resultBlob != IntPtr.Zero) hb_blob_destroy(resultBlob);
            if (resultFace != IntPtr.Zero) hb_face_destroy(resultFace);
            hb_subset_input_destroy(input);
        }
    }
}
