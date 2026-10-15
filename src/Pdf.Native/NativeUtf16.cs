using System.Runtime.InteropServices;

namespace Pdf.Native;

internal static class NativeUtf16
{
    /// <summary>길이 질의(바이트) → 버퍼 할당 → 다시 호출 → UTF-16LE 해석, 끝의 NUL 제거.</summary>
    public static string Read(Func<IntPtr, nuint, nuint> call)
    {
        nuint bytes = call(IntPtr.Zero, 0);
        if (bytes <= 2) return string.Empty;
        IntPtr buf = Marshal.AllocHGlobal((int)bytes);
        try
        {
            nuint got = call(buf, bytes);
            if (got <= 2) return string.Empty;
            return Marshal.PtrToStringUni(buf, (int)(got / 2)).TrimEnd('\0');
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
