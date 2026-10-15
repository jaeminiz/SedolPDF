using System.Drawing;
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
        => OpenBytes(t, File.ReadAllBytes(path), password);

    /// <summary>바이트 배열을 메모리에서 연다. 배열은 문서 수명 동안 고정된다.</summary>
    public static PdfDocument OpenBytes(PdfiumThread t, byte[] data, string? password = null)
    {
        t.AssertOnThread();
        PdfiumLibrary.EnsureInitialized(t);
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
        InsertBlankPage(NativeMethods.FPDF_GetPageCount(_handle), widthPt, heightPt);
    }

    /// <summary>index 위치에 빈 페이지를 넣는다 (크기는 포인트).</summary>
    public void InsertBlankPage(int index, double widthPt, double heightPt)
    {
        Check();
        var page = NativeMethods.FPDFPage_New(_handle, index, widthPt, heightPt);
        if (page == IntPtr.Zero) throw PdfiumException.FromLastError("페이지 추가");
        NativeMethods.FPDF_ClosePage(page);
    }

    /// <summary>source의 sourceIndices 쪽들을 주어진 순서대로 insertAt 위치에 가져온다 (주석 포함).</summary>
    public void ImportPages(PdfDocument source, IReadOnlyList<int> sourceIndices, int insertAt)
    {
        Check();
        var src = source.Handle;
        var arr = sourceIndices.ToArray();
        int ok;
        fixed (int* p = arr)
            ok = NativeMethods.FPDF_ImportPagesByIndex(_handle, src, p, (ulong)arr.Length, insertAt);
        if (ok == 0) throw PdfiumException.FromLastError("페이지 가져오기");
    }

    public void DeletePage(int index)
    {
        Check();
        NativeMethods.FPDFPage_Delete(_handle, index);
    }

    /// <summary>옮긴 페이지들이 주어진 순서대로 연속해서 놓이고 첫 페이지의 결과 인덱스가 destIndex가 된다.</summary>
    public void MovePages(IReadOnlyList<int> indices, int destIndex)
    {
        Check();
        var arr = indices.ToArray();
        int ok;
        fixed (int* p = arr)
            ok = NativeMethods.FPDF_MovePages(_handle, p, (ulong)arr.Length, destIndex);
        if (ok == 0) throw PdfiumException.FromLastError("페이지 이동");
    }

    /// <summary>페이지를 로드하지 않고 크기(포인트)를 얻는다. 회전 반영 여부는 테스트로 확인.</summary>
    public SizeF GetPageSizePt(int index)
    {
        Check();
        FsSizeF s;
        if (NativeMethods.FPDF_GetPageSizeByIndexF(_handle, index, &s) == 0)
            throw PdfiumException.FromLastError("페이지 크기");
        return new SizeF(s.Width, s.Height);
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
