using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace WhiteboardExporter.App.Services;

/// <summary>
/// Feeds consecutive BGRA scanlines to the Windows PNG/JPEG encoder without
/// constructing a full-size BitmapSource. Callers own the temporary output.
/// Native interface order/GUIDs follow the Windows SDK's wincodec.idl.
/// https://learn.microsoft.com/windows/win32/wic/-wic-imp-iwicbitmapframeencode
/// </summary>
internal sealed class RasterImageWriter : IDisposable
{
    private readonly int width;
    private readonly int height;
    private readonly bool jpeg;
    private IStream? stream;
    private IWicBitmapEncoder? encoder;
    private IWicBitmapFrameEncode? frame;
    private byte[]? jpegRows;
    private int writtenRows;
    private bool completed;
    private bool disposed;

    public RasterImageWriter(string path, int width, int height, bool jpeg)
    {
        if (width is < 1 or > 20000 || height is < 1 or > 20000)
            throw new ArgumentOutOfRangeException(nameof(width), "图片两轴必须在 1–20000 像素之间。");
        this.width = width;
        this.height = height;
        this.jpeg = jpeg;
        IPropertyBag2? options = null;
        try
        {
            // STGM_WRITE | STGM_SHARE_EXCLUSIVE; fCreate refuses an existing file.
            // A native disk stream avoids retaining encoded bytes in managed RAM.
            Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(Path.GetFullPath(path), 0x11, 0x80, true, IntPtr.Zero, out stream));
            var clsid = new Guid(jpeg ? "1a34f5c1-4a5a-46dc-b644-1f4567e7a676" : "27949969-876a-41d7-9447-568f6a35a4dc");
            encoder = (IWicBitmapEncoder)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, true)!)!;
            // WICBitmapEncoderNoCache: encode arriving scanlines instead of caching
            // the 1.6 GiB pixel raster of a 20000 x 20000 BGRA image.
            encoder.Initialize(stream, 2);
            encoder.CreateNewFrame(out frame, out options);
            if (jpeg && options is not null)
                options.Write(1, [new PropertyBag { Name = "ImageQuality" }], [0.95f]);
            frame.Initialize(options);
            frame.SetSize((uint)width, (uint)height);
            frame.SetResolution(96, 96);
            var requested = new Guid(jpeg ? "6fddc324-4e03-4bfe-b185-3d77768dc90c" : "6fddc324-4e03-4bfe-b185-3d77768dc90f");
            var actual = requested;
            frame.SetPixelFormat(ref actual);
            if (actual != requested) throw new IOException("Windows 图片编码器未接受所需的像素格式。");
        }
        catch
        {
            Dispose();
            throw;
        }
        finally { Release(options); }
    }

    public void WriteRows(byte[] bgra, int rows, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed) throw new InvalidOperationException("图片已完成编码。");
        ArgumentNullException.ThrowIfNull(bgra);
        if (rows < 1 || rows > height - writtenRows) throw new ArgumentOutOfRangeException(nameof(rows));
        var pixels = checked(width * rows);
        var bgraBytes = checked(pixels * 4);
        if (bgra.Length < bgraBytes) throw new ArgumentException("扫描行像素缓冲区不足。", nameof(bgra));
        cancellationToken.ThrowIfCancellationRequested();
        if (jpeg)
        {
            var length = checked(pixels * 3);
            if (jpegRows is null || jpegRows.Length < length) jpegRows = new byte[length];
            // JPEG has no alpha. Composite on white even when the input contains
            // transparent pixels; BGRA input is straight, not premultiplied.
            for (int pixel = 0, source = 0, target = 0; pixel < pixels; pixel++, source += 4, target += 3)
            {
                if ((pixel & 0x3ffff) == 0) cancellationToken.ThrowIfCancellationRequested();
                var alpha = bgra[source + 3];
                if (alpha == 255)
                {
                    jpegRows[target] = bgra[source];
                    jpegRows[target + 1] = bgra[source + 1];
                    jpegRows[target + 2] = bgra[source + 2];
                }
                else
                {
                    var white = 255 * (255 - alpha) + 127;
                    jpegRows[target] = (byte)((bgra[source] * alpha + white) / 255);
                    jpegRows[target + 1] = (byte)((bgra[source + 1] * alpha + white) / 255);
                    jpegRows[target + 2] = (byte)((bgra[source + 2] * alpha + white) / 255);
                }
            }
            frame!.WritePixels((uint)rows, (uint)(width * 3), (uint)length, jpegRows);
        }
        else frame!.WritePixels((uint)rows, (uint)(width * 4), (uint)bgraBytes, bgra);
        writtenRows += rows;
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Complete(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed) throw new InvalidOperationException("图片已完成编码。");
        if (writtenRows != height) throw new IOException("图片扫描行尚未全部写入，不能完成输出。");
        cancellationToken.ThrowIfCancellationRequested();
        frame!.Commit();
        encoder!.Commit();
        stream!.Commit(0);
        cancellationToken.ThrowIfCancellationRequested();
        completed = true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Release(frame); frame = null;
        Release(encoder); encoder = null;
        Release(stream); stream = null;
        jpegRows = null;
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes,
        [MarshalAs(UnmanagedType.Bool)] bool create, IntPtr template, [MarshalAs(UnmanagedType.Interface)] out IStream stream);

    // Unused methods retain native vtable slots; their pointer arguments are not
    // dereferenced. Only interfaces created privately by this writer are released.
    [ComImport, Guid("00000103-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicBitmapEncoder
    {
        void Initialize(IStream stream, uint cacheOption);
        void GetContainerFormat(out Guid format);
        void GetEncoderInfo(out IntPtr info);
        void SetColorContexts(uint count, IntPtr contexts);
        void SetPalette(IntPtr palette);
        void SetThumbnail(IntPtr source);
        void SetPreview(IntPtr source);
        void CreateNewFrame(out IWicBitmapFrameEncode frame, out IPropertyBag2 options);
        void Commit();
        void GetMetadataQueryWriter(out IntPtr writer);
    }

    [ComImport, Guid("00000105-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicBitmapFrameEncode
    {
        void Initialize(IPropertyBag2? options);
        void SetSize(uint width, uint height);
        void SetResolution(double dpiX, double dpiY);
        void SetPixelFormat(ref Guid format);
        void SetColorContexts(uint count, IntPtr contexts);
        void SetPalette(IntPtr palette);
        void SetThumbnail(IntPtr source);
        void WritePixels(uint rows, uint stride, uint size, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] pixels);
        void WriteSource(IntPtr source, IntPtr rectangle);
        void Commit();
        void GetMetadataQueryWriter(out IntPtr writer);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PropertyBag
    {
        public uint Type;
        public ushort VariantType;
        public ushort ClipboardFormat;
        public uint Hint;
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        public Guid ClassId;
    }

    [ComImport, Guid("22f55882-280b-11d0-a8a9-00a0c90c2004"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag2
    {
        void Read(uint count, IntPtr properties, IntPtr errorLog, IntPtr values, IntPtr errors);
        void Write(uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] PropertyBag[] properties,
            [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Struct, SizeParamIndex = 0)] object[] values);
        void CountProperties(out uint count);
        void GetPropertyInfo(uint index, uint count, IntPtr properties, out uint actual);
        void LoadObject([MarshalAs(UnmanagedType.LPWStr)] string name, uint hint, IntPtr unknown, IntPtr errorLog);
    }
}
