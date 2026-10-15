using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PdfEditor.App.Viewer;

/// <summary>WriteableBitmap(BGRA, 불투명)을 감싼 IImageHandle. UI 스레드에서 만들고 없앤다.</summary>
public sealed class BitmapHandle : IImageHandle
{
    public BitmapHandle(byte[] bgra, int width, int height)
    {
        Width = width;
        Height = height;
        Bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = Bitmap.Lock();
        int rowBytes = width * 4;
        for (int y = 0; y < height; y++)
            Marshal.Copy(bgra, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
    }

    public WriteableBitmap Bitmap { get; }
    public int Width { get; }
    public int Height { get; }
    public void Dispose() => Bitmap.Dispose();
}
