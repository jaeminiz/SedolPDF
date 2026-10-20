using System.Runtime.InteropServices;

namespace PdfEditor.App.Viewer;

/// <summary>
/// Windows 클립보드에 그림을 직접 넣는다(2026-10-20 사용자 보고: 그림판은 되는데 카카오톡에 붙여넣으면 검정).
/// 24비트 불투명 DIB(CF_DIB, 알파 없음)와 "PNG" 형식을 함께 넣어 알파를 해석하는 프로그램에서도 검정이 되지 않게 한다.
/// UI 스레드에서 부른다. 클립보드가 다른 프로그램에 잡혀 있으면 잠깐 다시 시도하고, 그래도 안 되면 false.
/// </summary>
public static class WinClipboard
{
    private const uint CF_DIB = 8, GMEM_MOVEABLE = 0x0002;

    /// <summary>시험용: 지정하면 실제 클립보드 대신 이 함수가 받는다(시험이 사용자의 클립보드를 건드리지 않게).</summary>
    public static Func<byte[], int, int, byte[]?, bool>? TestSink { get; set; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr hMem);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>bgra: 위에서 아래로, 픽셀당 4바이트(B,G,R,A). 알파는 무시하고 흰 바탕에 합성한 24비트 DIB를 만든다. png: 같은 그림의 PNG 바이트(없으면 null).</summary>
    public static bool SetImage(byte[] bgra, int w, int h, byte[]? png)
    {
        if (w <= 0 || h <= 0 || bgra.Length < w * h * 4) return false;
        if (TestSink is { } sink) return sink(bgra, w, h, png);
        byte[] dib = BuildDib(bgra, w, h);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    bool ok = Put(CF_DIB, dib);
                    if (png is not null) Put(RegisterClipboardFormat("PNG"), png);
                    return ok;
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(30);
        }
        return false;
    }

    public static byte[] BuildDib(byte[] bgra, int w, int h)
    {
        int stride = (w * 3 + 3) & ~3;
        var dib = new byte[40 + stride * h];
        BitConverter.TryWriteBytes(dib.AsSpan(0), 40);          // biSize
        BitConverter.TryWriteBytes(dib.AsSpan(4), w);           // biWidth
        BitConverter.TryWriteBytes(dib.AsSpan(8), h);           // biHeight(양수 = 아래에서 위로)
        BitConverter.TryWriteBytes(dib.AsSpan(12), (short)1);   // biPlanes
        BitConverter.TryWriteBytes(dib.AsSpan(14), (short)24);  // biBitCount
        BitConverter.TryWriteBytes(dib.AsSpan(20), stride * h); // biSizeImage
        for (int y = 0; y < h; y++)
        {
            int src = y * w * 4, dst = 40 + (h - 1 - y) * stride;
            for (int x = 0; x < w; x++)
            {
                int a = bgra[src + 3];
                // 알파가 있으면 흰 바탕에 합성(곱한 알파가 아닌 값으로 가정)
                dib[dst] = (byte)((bgra[src] * a + 255 * (255 - a)) / 255);
                dib[dst + 1] = (byte)((bgra[src + 1] * a + 255 * (255 - a)) / 255);
                dib[dst + 2] = (byte)((bgra[src + 2] * a + 255 * (255 - a)) / 255);
                src += 4; dst += 3;
            }
        }
        return dib;
    }

    private static bool Put(uint format, byte[] data)
    {
        IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (hMem == IntPtr.Zero) return false;
        IntPtr p = GlobalLock(hMem);
        if (p == IntPtr.Zero) { GlobalFree(hMem); return false; }
        try { Marshal.Copy(data, 0, p, data.Length); }
        finally { GlobalUnlock(hMem); }
        if (SetClipboardData(format, hMem) == IntPtr.Zero) { GlobalFree(hMem); return false; }   // 성공하면 클립보드가 메모리를 소유한다
        return true;
    }
}
