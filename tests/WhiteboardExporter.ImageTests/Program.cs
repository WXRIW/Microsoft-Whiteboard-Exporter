using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhiteboardExporter.App.Services;
using WhiteboardExporter.App;

internal static class Program
{
    private static string output = "";
    private static int passed;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--preview") return PreviewMemory(args[1]);
        if (args.Length == 3 && args[0] == "--compare")
        {
            try
            {
                Require(RasterComparison.Equivalent(args[1], args[2]), "Seam fixture exceeds the bounded AA tolerance");
                Require(RasterComparison.VerifyRejectsGapsAndShifts(args[1], args[2]), "Comparator accepted a one-pixel gap or region shift");
                Console.WriteLine("PASS seam fixture equivalent; all four one-pixel gap/shift mutants rejected");
                return 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        }
        output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("artifacts", "image-writer-tests"));
        Directory.CreateDirectory(output);
        try
        {
            Check("PNG preserves straight BGRA, row order, odd width and 96 DPI", PngPixels);
            Check("JPEG composites transparency on white, odd width and 96 DPI", JpegPixels);
            Check("Incomplete raster cannot commit", Incomplete);
            Check("Disposed and completed writers reject further writes", Lifetime);
            Check("Cancelled row/commit stages preserve prior destination", CancellationAtomic);
            Check("Invalid dimensions and insufficient rows are rejected", InvalidInput);
            Check("Native PNG/JPEG scratch commits correctly beyond MAX_PATH", LongPathNativeOutput);
            Check("Cancelled and failed native commits preserve old output and clean staging", NativeCommitFailure);
            Check("20000 x 20000 PNG streams to disk within bounded memory", () => Large(false));
            Check("20000 x 20000 JPEG streams to disk within bounded memory", () => Large(true));
            Console.WriteLine($"{passed}/10 passed; synthetic output: {output}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAILED after {passed} passes: {exception}");
            return 1;
        }
    }

    private static void Check(string title, Action action)
    {
        action();
        passed++;
        Console.WriteLine($"PASS {title}");
    }

    private static void LongPathNativeOutput()
    {
        var directory = Path.Combine(output, "native-long-path");
        while (Path.Combine(directory, "result.png").Length < 320)
            directory = Path.Combine(directory, "中文账号与白板目录-" + new string('x', 32));
        Directory.CreateDirectory(directory);
        foreach (var jpeg in new[] { false, true })
        {
            var target = Path.Combine(directory, jpeg ? "result.jpg" : "result.png");
            File.WriteAllBytes(target, [17, 24, 99, 138]);
            string scratch;
            using (var temporary = new NativeExportFile(jpeg ? ".jpg" : ".png"))
            {
                scratch = temporary.Path;
                Require(scratch.Length < 240, "Native encoder received a long scratch path");
                using (var writer = new RasterImageWriter(scratch, 7, 5, jpeg))
                {
                    var pixels = new byte[7 * 5 * 4];
                    for (var i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 255; pixels[i + 3] = 255; }
                    writer.WriteRows(pixels, 5); writer.Complete();
                }
                temporary.CommitAsync(target).GetAwaiter().GetResult();
            }
            var decoded = DecodeSmall(target, out var width, out var height, out _, out _);
            Require(width == 7 && height == 5, "Long-path output dimensions changed");
            AssertPixel(decoded, width, 3, 2, 0, 0, 255, jpeg ? 12 : 0);
            Require(!File.Exists(scratch), "Completed native scratch was not removed");
            Require(Directory.GetFiles(directory, ".wbex-*.tmp").Length == 0, "Completed staging was not removed");
        }
    }

    private static void NativeCommitFailure()
    {
        var directory = Path.Combine(output, "native-commit-failure");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "existing.pdf");
        var original = new byte[] { 17, 24, 99, 138 };
        foreach (var cancel in new[] { false, true })
        {
            File.WriteAllBytes(target, original);
            string scratch;
            using (var temporary = new NativeExportFile(".pdf"))
            {
                scratch = temporary.Path;
                File.WriteAllBytes(scratch, "%PDF-1.7 native-completed-output"u8.ToArray());
                if (cancel)
                {
                    using var token = new CancellationTokenSource(); token.Cancel();
                    Throws<OperationCanceledException>(() => temporary.CommitAsync(target, token.Token).GetAwaiter().GetResult());
                }
                else
                {
                    // Windows refuses replacement while the prior file disallows delete.
                    using var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                    try
                    {
                        temporary.CommitAsync(target).GetAwaiter().GetResult();
                        throw new InvalidOperationException("Locked destination replacement was accepted");
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }
            Require(File.ReadAllBytes(target).SequenceEqual(original), "A failed commit modified the prior destination");
            Require(!File.Exists(scratch), "Cancelled/failed native scratch was not removed");
            Require(Directory.GetFiles(directory, ".wbex-*.tmp").Length == 0, "Cancelled/failed staging was not removed");
        }
    }

    private static void PngPixels()
    {
        const int width = 73, height = 37;
        var original = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            original[i] = (byte)(x * 3);
            original[i + 1] = (byte)(y * 6);
            original[i + 2] = (byte)(x + y);
            original[i + 3] = (byte)(x < 24 ? 255 : x < 48 ? 128 : 0);
        }
        var path = Fresh("small-bgra.png");
        using (var writer = new RasterImageWriter(path, width, height, false))
        {
            var start = 0;
            foreach (var rows in new[] { 1, 7, 13, 16 })
            {
                writer.WriteRows(original.AsSpan(start * width * 4, rows * width * 4).ToArray(), rows);
                start += rows;
            }
            writer.Complete();
        }
        var decoded = DecodeSmall(path, out var actualWidth, out var actualHeight, out var dpiX, out var dpiY);
        Require(actualWidth == width && actualHeight == height, "PNG dimensions changed");
        Require(Math.Abs(dpiX - 96) < .1 && Math.Abs(dpiY - 96) < .1, "PNG resolution changed");
        Require(original.SequenceEqual(decoded), "PNG BGRA pixels or band order changed");
    }

    private static void JpegPixels()
    {
        const int width = 73, height = 37;
        var original = new byte[width * height * 4];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var x = pixel % width;
            original[pixel * 4] = (byte)(x >= 48 ? 255 : 0);
            original[pixel * 4 + 2] = (byte)(x < 48 ? 255 : 0);
            original[pixel * 4 + 3] = (byte)(x < 24 ? 255 : x < 48 ? 128 : 0);
        }
        var path = Fresh("small-alpha-white.jpg");
        using (var writer = new RasterImageWriter(path, width, height, true))
        {
            writer.WriteRows(original.AsSpan(0, width * 31 * 4).ToArray(), 31);
            // A larger reusable band is allowed: only the requested final six rows are encoded.
            var finalBand = new byte[width * 16 * 4];
            original.AsSpan(width * 31 * 4).CopyTo(finalBand);
            writer.WriteRows(finalBand, 6);
            writer.Complete();
        }
        var decoded = DecodeSmall(path, out var actualWidth, out var actualHeight, out var dpiX, out var dpiY);
        Require(actualWidth == width && actualHeight == height, "JPEG dimensions changed");
        Require(Math.Abs(dpiX - 96) < .1 && Math.Abs(dpiY - 96) < .1, "JPEG resolution changed");
        AssertPixel(decoded, width, 10, 35, 0, 0, 255, 12);
        AssertPixel(decoded, width, 36, 35, 127, 127, 255, 12);
        AssertPixel(decoded, width, 62, 35, 255, 255, 255, 12);
    }

    private static void Incomplete()
    {
        var path = Fresh("incomplete.png");
        using (var writer = new RasterImageWriter(path, 7, 5, false))
        {
            writer.WriteRows(new byte[7 * 3 * 4], 3);
            Throws<IOException>(() => writer.Complete());
        }
        // The helper deliberately owns only the temporary file; the caller removes unfinished output.
        File.Delete(path);
        Require(!File.Exists(path), "Disposed encoder retained its file handle");
    }

    private static void Lifetime()
    {
        var path = Fresh("lifetime.png");
        var writer = new RasterImageWriter(path, 1, 1, false);
        writer.WriteRows([0, 0, 255, 255], 1);
        writer.Complete();
        Throws<InvalidOperationException>(() => writer.WriteRows([0, 0, 0, 0], 1));
        Throws<InvalidOperationException>(() => writer.Complete());
        writer.Dispose();
        writer.Dispose();
        Throws<ObjectDisposedException>(() => writer.WriteRows([0, 0, 0, 0], 1));
    }

    private static void CancellationAtomic()
    {
        foreach (var commit in new[] { false, true })
        {
            var target = Path.Combine(output, $"cancel-{commit}.png");
            var original = new byte[] { 17, 24, 99, 138 };
            File.WriteAllBytes(target, original);
            var temporary = Fresh($"cancel-{commit}.pending.png");
            using var cancellation = new CancellationTokenSource();
            try
            {
                using (var writer = new RasterImageWriter(temporary, 4, 2, true))
                {
                    if (commit) writer.WriteRows(new byte[4 * 2 * 4], 2);
                    cancellation.Cancel();
                    if (commit) writer.Complete(cancellation.Token);
                    else writer.WriteRows(new byte[4 * 2 * 4], 2, cancellation.Token);
                }
                File.Move(temporary, target, true);
                throw new InvalidOperationException("Cancellation was ignored");
            }
            catch (OperationCanceledException) { }
            finally { File.Delete(temporary); }
            Require(File.ReadAllBytes(target).SequenceEqual(original), "Cancelled stage replaced prior destination");
            Require(!File.Exists(temporary), "Cancelled temporary output was retained");
        }
    }

    private static void InvalidInput()
    {
        Throws<ArgumentOutOfRangeException>(() => new RasterImageWriter(Path.Combine(output, "invalid.png"), 20001, 1, false));
        Throws<ArgumentOutOfRangeException>(() => new RasterImageWriter(Path.Combine(output, "zero.png"), 1, 0, false));
        using var writer = new RasterImageWriter(Fresh("input-check.png"), 11, 3, false);
        Throws<ArgumentException>(() => writer.WriteRows(new byte[43], 1));
        Throws<ArgumentOutOfRangeException>(() => writer.WriteRows(new byte[44], 4));
    }

    private static void Large(bool jpeg)
    {
        const int width = 20000, height = 20000, bandRows = 64;
        var path = Fresh(jpeg ? "synthetic-20000.jpg" : "synthetic-20000.png");
        var band = new byte[width * bandRows * 4];
        var timer = Stopwatch.StartNew();
        using (var writer = new RasterImageWriter(path, width, height, jpeg))
        {
            for (var top = 0; top < height; top += bandRows)
            {
                var rows = Math.Min(bandRows, height - top);
                for (var y = 0; y < rows; y++)
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    band[i] = (byte)((x / 256 + (top + y) / 256) * 17);
                    band[i + 1] = (byte)((x / 256) * 29);
                    band[i + 2] = (byte)(((top + y) / 256) * 31);
                    band[i + 3] = 255;
                }
                writer.WriteRows(band, rows);
            }
            writer.Complete();
        }
        var dimensions = jpeg ? JpegDimensions(path) : PngDimensions(path);
        Require(dimensions == (width, height), "400 MP output was silently resized");
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var peakMiB = process.PeakWorkingSet64 / (1024d * 1024);
        Require(peakMiB < 512, $"Native writer used excessive peak memory: {peakMiB:F1} MiB");
        Console.WriteLine($"  {(jpeg ? "JPEG" : "PNG")}: {width} x {height}, {new FileInfo(path).Length:N0} bytes, {timer.Elapsed.TotalSeconds:F1}s, process peak {peakMiB:F1} MiB (large image never decoded)");
    }

    private static int PreviewMemory(string path)
    {
        try
        {
            var timer = Stopwatch.StartNew();
            var uri = new Uri(Path.GetFullPath(path));
            var header = BitmapFrame.Create(uri, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var width = header.PixelWidth; var height = header.PixelHeight;
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = uri;
            if (Math.Max(width, height) > 4096)
            {
                if (width >= height) bitmap.DecodePixelWidth = 4096;
                else bitmap.DecodePixelHeight = 4096;
            }
            bitmap.EndInit(); bitmap.Freeze();
            // Force the actual scaled pixels to be materialized, as an image control would.
            var stride = checked((bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8);
            var scaled = new byte[checked(stride * bitmap.PixelHeight)];
            bitmap.CopyPixels(scaled, stride, 0);
            Require(Math.Max(bitmap.PixelWidth, bitmap.PixelHeight) <= 4096, "Preview retained original dimensions");
            using var process = Process.GetCurrentProcess(); process.Refresh();
            var peakMiB = process.PeakWorkingSet64 / (1024d * 1024);
            Console.WriteLine($"Preview: {width} x {height} → {bitmap.PixelWidth} x {bitmap.PixelHeight}, {timer.Elapsed.TotalSeconds:F1}s, process peak {peakMiB:F1} MiB");
            Require(peakMiB < 512, "WPF scaled preview exceeded the test memory budget");
            GC.KeepAlive(bitmap); GC.KeepAlive(scaled);
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static byte[] DecodeSmall(string path, out int width, out int height, out double dpiX, out double dpiY)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        width = frame.PixelWidth; height = frame.PixelHeight;
        dpiX = frame.DpiX; dpiY = frame.DpiY;
        Require(width * height < 10000, "Tests may fully decode small fixtures only");
        BitmapSource source = frame.Format == PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var result = new byte[width * height * 4];
        source.CopyPixels(result, width * 4, 0);
        return result;
    }

    private static (int Width, int Height) PngDimensions(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);
        Require(header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && header[12..16].SequenceEqual("IHDR"u8), "Invalid PNG header");
        return (BinaryPrimitives.ReadInt32BigEndian(header[16..20]), BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
    }

    private static (int Width, int Height) JpegDimensions(string path)
    {
        using var stream = File.OpenRead(path);
        Require(stream.ReadByte() == 255 && stream.ReadByte() == 216, "Invalid JPEG header");
        Span<byte> pair = stackalloc byte[2];
        Span<byte> dimensions = stackalloc byte[5];
        while (stream.Position < stream.Length)
        {
            Require(stream.ReadByte() == 255, "Invalid JPEG marker");
            var marker = stream.ReadByte();
            while (marker == 255) marker = stream.ReadByte();
            Require(marker is >= 0 and <= 255 && marker != 218 && marker != 217, "JPEG lacks dimensions");
            if (marker is 1 or >= 208 and <= 215) continue;
            stream.ReadExactly(pair);
            var length = BinaryPrimitives.ReadUInt16BigEndian(pair);
            Require(length >= 2, "Invalid JPEG segment length");
            if (marker is >= 192 and <= 195 or >= 197 and <= 199 or >= 201 and <= 203 or >= 205 and <= 207)
            {
                Require(length >= 7, "Invalid JPEG frame dimensions");
                stream.ReadExactly(dimensions);
                return (BinaryPrimitives.ReadUInt16BigEndian(dimensions[3..5]), BinaryPrimitives.ReadUInt16BigEndian(dimensions[1..3]));
            }
            stream.Seek(length - 2, SeekOrigin.Current);
        }
        throw new InvalidDataException("JPEG lacks a frame header");
    }

    private static void AssertPixel(byte[] pixels, int width, int x, int y, int blue, int green, int red, int tolerance)
    {
        var i = (y * width + x) * 4;
        Require(Math.Abs(pixels[i] - blue) <= tolerance && Math.Abs(pixels[i + 1] - green) <= tolerance && Math.Abs(pixels[i + 2] - red) <= tolerance && pixels[i + 3] == 255,
            $"JPEG pixel ({x},{y}) expected BGRA ({blue},{green},{red},255); got ({pixels[i]},{pixels[i + 1]},{pixels[i + 2]},{pixels[i + 3]})");
    }

    private static string Fresh(string name)
    {
        var path = Path.Combine(output, name);
        File.Delete(path);
        return path;
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidDataException(reason);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidDataException($"Expected {typeof(T).Name}");
    }
}
