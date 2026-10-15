using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Pdf.Native.Interop;

namespace Pdf.Native;

public sealed unsafe class PdfDocument : IDisposable
{
    private readonly PdfiumThread _t;
    private IntPtr _handle;
    private GCHandle _pin;   // LoadMemDocument64 버퍼는 문서 수명 동안 고정

    private PdfDocument(PdfiumThread t, IntPtr handle, GCHandle pin)
    {
        _t = t;
        _handle = handle;
        _pin = pin;
    }

    internal IntPtr Handle { get { Check(); return _handle; } }

    /// <summary>파일을 .NET으로 읽어 메모리에서 연다 (한글·공백 경로 대응).</summary>
    public static PdfDocument Open(PdfiumThread t, string path, string? password = null)
    {
        t.AssertOnThread();
        PdfiumLibrary.EnsureInitialized(t);
        var data = File.ReadAllBytes(path);
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        IntPtr doc;
        try
        {
            byte[]? pw = password is null ? null : Encoding.UTF8.GetBytes(password + "\0");
            fixed (byte* pwp = pw)
                doc = NativeMethods.FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (nuint)data.Length, pwp);
            if (doc == IntPtr.Zero) throw PdfiumException.FromLastError("문서 열기");
        }
        catch
        {
            pin.Free();
            throw;
        }
        return new PdfDocument(t, doc, pin);
    }

    public static PdfDocument CreateNew(PdfiumThread t)
    {
        t.AssertOnThread();
        PdfiumLibrary.EnsureInitialized(t);
        var doc = NativeMethods.FPDF_CreateNewDocument();
        if (doc == IntPtr.Zero) throw PdfiumException.FromLastError("새 문서 만들기");
        return new PdfDocument(t, doc, default);
    }

    public int PageCount { get { Check(); return NativeMethods.FPDF_GetPageCount(_handle); } }

    public PdfPage LoadPage(int index)
    {
        Check();
        var page = NativeMethods.FPDF_LoadPage(_handle, index);
        if (page == IntPtr.Zero) throw PdfiumException.FromLastError("페이지 불러오기");
        return new PdfPage(_t, page);
    }

    /// <summary>문서 맨 끝에 빈 페이지를 추가한다 (크기는 포인트).</summary>
    public void AddPage(double widthPt, double heightPt)
    {
        Check();
        var page = NativeMethods.FPDFPage_New(_handle, NativeMethods.FPDF_GetPageCount(_handle), widthPt, heightPt);
        if (page == IntPtr.Zero) throw PdfiumException.FromLastError("페이지 추가");
        NativeMethods.FPDF_ClosePage(page);
    }

    /// <summary>전체 저장 (증분 아님).</summary>
    public void SaveCopy(string path)
    {
        Check();
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        var gc = GCHandle.Alloc(fs);
        try
        {
            var writer = new FpdfFileWrite { Version = 1, WriteBlock = &WriteBlock, Handle = GCHandle.ToIntPtr(gc) };
            if (NativeMethods.FPDF_SaveAsCopy(_handle, &writer, 0) == 0)
                throw PdfiumException.FromLastError("저장");
        }
        finally { gc.Free(); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteBlock(FpdfFileWrite* self, void* data, nuint size)
    {
        try
        {
            var fs = (FileStream)GCHandle.FromIntPtr(self->Handle).Target!;
            fs.Write(new ReadOnlySpan<byte>(data, checked((int)size)));
            return 1;
        }
        catch { return 0; }
    }

    private void Check()
    {
        _t.AssertOnThread();
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        var h = _handle;
        var pin = _pin;
        _handle = IntPtr.Zero;
        RunOnThread(_t, () =>
        {
            NativeMethods.FPDF_CloseDocument(h);
            if (pin.IsAllocated) pin.Free();
        });
    }

    /// <summary>Dispose는 어느 스레드에서 불러도 PDFium 호출은 전용 스레드로 보낸다. 스레드가 이미 닫혔으면 건너뜀.</summary>
    internal static void RunOnThread(PdfiumThread t, Action a)
    {
        try { t.InvokeAsync(a).GetAwaiter().GetResult(); }
        catch (ObjectDisposedException) { }
        catch (TaskCanceledException) { }
    }
}
