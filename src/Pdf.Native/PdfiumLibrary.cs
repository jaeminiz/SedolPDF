using Pdf.Native.Interop;

namespace Pdf.Native;

public static class PdfiumLibrary
{
    private static bool _initialized;

    /// <summary>FPDF_InitLibraryWithConfig를 프로세스 안에서 한 번만 호출한다.</summary>
    public static unsafe void EnsureInitialized(PdfiumThread t)
    {
        t.AssertOnThread();
        if (_initialized) return;
        var config = new FpdfLibraryConfig { Version = 2 };
        NativeMethods.FPDF_InitLibraryWithConfig(&config);
        _initialized = true;
    }
}
