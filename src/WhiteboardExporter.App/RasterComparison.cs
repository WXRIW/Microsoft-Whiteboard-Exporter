using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WhiteboardExporter.App;

/// <summary>
/// Test-only comparison for public seam and viewport fixtures. SVG rasterization
/// can quantize Skia edge coverage differently between captures.
/// This helper never accepts production exports or decodes 400 MP test images.
/// </summary>
internal static class RasterComparison
{
    private const int MaximumComparisonPixels = 8_000_000;
    private const int MaximumEdgeDelta = 32;
    private const int EdgeNeighborhoodSpan = 8;
    private const double MaximumDifferentFraction = .0015;

    public static bool Equivalent(string actual, string reference, double maximumDifferentFraction = MaximumDifferentFraction) => Equivalent(Read(actual), Read(reference), maximumDifferentFraction);

    public static bool VerifyRejectsGapsAndShifts(string actual, string reference, int seamX = 4096, int seamY = 512)
    {
        var source = Read(actual);
        var expected = Read(reference);
        if (!Equivalent(source, expected) || seamX < 1 || seamX >= source.Width || seamY < 1 || seamY >= source.Height) return false;
        var stride = checked(source.Width * 4);
        var changed = (byte[])source.Pixels.Clone();
        // A missing vertical scanline includes solid interior pixels.
        for (var y = 0; y < source.Height; y++) Array.Fill(changed, (byte)255, y * stride + seamX * 4, 4);
        if (Equivalent(source with { Pixels = changed }, expected)) return false;
        changed = (byte[])source.Pixels.Clone();
        Array.Fill(changed, (byte)255, seamY * stride, stride);
        if (Equivalent(source with { Pixels = changed }, expected)) return false;
        changed = (byte[])source.Pixels.Clone();
        // Shift the entire right capture region by one pixel, not just its edge.
        for (var y = 0; y < source.Height; y++)
        {
            Buffer.BlockCopy(source.Pixels, y * stride + seamX * 4, changed, y * stride + (seamX + 1) * 4, (source.Width - seamX - 1) * 4);
            Array.Fill(changed, (byte)255, y * stride + seamX * 4, 4);
        }
        if (Equivalent(source with { Pixels = changed }, expected)) return false;
        changed = (byte[])source.Pixels.Clone();
        Buffer.BlockCopy(source.Pixels, seamY * stride, changed, (seamY + 1) * stride, (source.Height - seamY - 1) * stride);
        Array.Fill(changed, (byte)255, seamY * stride, stride);
        return !Equivalent(source with { Pixels = changed }, expected);
    }

    private static bool Equivalent(Raster actual, Raster reference, double maximumDifferentFraction = MaximumDifferentFraction)
    {
        if (actual.Width != reference.Width || actual.Height != reference.Height) return false;
        var budget = (int)Math.Floor((long)actual.Width * actual.Height * maximumDifferentFraction);
        var different = 0;
        for (var y = 0; y < actual.Height; y++)
        for (var x = 0; x < actual.Width; x++)
        {
            var offset = (y * actual.Width + x) * 4;
            if (actual.Pixels[offset + 3] != reference.Pixels[offset + 3]) return false;
            var delta = 0;
            for (var channel = 0; channel < 3; channel++)
                delta = Math.Max(delta, Math.Abs(actual.Pixels[offset + channel] - reference.Pixels[offset + channel]));
            if (delta == 0) continue;
            if (++different > budget || delta > MaximumEdgeDelta || delta > 1 && !IsEdge(reference, x, y)) return false;
        }
        return true;
    }

    private static bool IsEdge(Raster reference, int x, int y)
    {
        for (var channel = 0; channel < 3; channel++)
        {
            var minimum = 255; var maximum = 0;
            for (var row = Math.Max(0, y - 1); row <= Math.Min(reference.Height - 1, y + 1); row++)
            for (var column = Math.Max(0, x - 1); column <= Math.Min(reference.Width - 1, x + 1); column++)
            {
                var value = reference.Pixels[(row * reference.Width + column) * 4 + channel];
                minimum = Math.Min(minimum, value); maximum = Math.Max(maximum, value);
            }
            if (maximum - minimum >= EdgeNeighborhoodSpan) return true;
        }
        return false;
    }

    private static Raster Read(string path)
    {
        // Inspect dimensions before an OnLoad decoder: only the small fixture is
        // compared here, never a complete 20000 x 20000 bitmap.
        var header = BitmapFrame.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (header.PixelWidth < 1 || header.PixelHeight < 1 || (long)header.PixelWidth * header.PixelHeight > MaximumComparisonPixels)
            throw new InvalidDataException("像素对比仅接受不超过 8MP 的合成测试图片。");
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(frame.PixelWidth * frame.PixelHeight * 4)];
        converted.CopyPixels(pixels, checked(frame.PixelWidth * 4), 0);
        return new Raster(frame.PixelWidth, frame.PixelHeight, pixels);
    }

    private sealed record Raster(int Width, int Height, byte[] Pixels);
}
