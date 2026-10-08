using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhiteboardExporter.Core;

namespace WhiteboardExporter.App.Services;

public sealed class DesktopExportService(LocalWebHost host) : IExportService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public const int MaximumImageDimension = 20_000;
    private const int CaptureTileWidth = 4096;
    private const int CaptureBandHeight = 512;
    public async Task<IReadOnlyList<string>> ExportAsync(BoardDocument document, ExportOptions options, string targetPath, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            targetPath = Path.GetFullPath(targetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            if (options.Format == ExportFormat.Archive)
            {
                await ArchiveService.SaveAsync(document, targetPath, cancellationToken);
                return [targetPath];
            }
            if (options.Format == ExportFormat.Json)
            {
                await ArchiveService.SaveJsonAsync(document, targetPath, cancellationToken);
                var files = new List<string> { targetPath };
                if (document.Scene["assets"] is System.Text.Json.Nodes.JsonObject assets)
                    foreach (var entry in assets)
                        if (entry.Value?["path"]?.GetValue<string>() is { } relative)
                        {
                            var assetPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(targetPath))!, relative));
                            if (File.Exists(assetPath)) files.Add(assetPath);
                        }
                return files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            if (document.Status == "failed") throw new InvalidDataException("此源文件尚不能重绘，可以导出原始备份；请查看内容检查。");
            await host.LoadAsync(document, cancellationToken);
            if (options.Format is ExportFormat.Svg or ExportFormat.Html)
            {
                var script = options.Format == ExportFormat.Svg ? "window.wb.exportSvg()" : "window.wb.exportHtml()";
                var value = (await host.EvaluateAsync(script, cancellationToken))?.GetValue<string>() ?? throw new IOException("无法生成离线文档。");
                await WriteAtomicallyAsync(targetPath, Encoding.UTF8.GetBytes(value), cancellationToken);
                return [targetPath];
            }
            if (options.Format == ExportFormat.Pdf)
            {
                var page = await host.EvaluateAsync("window.wb.preparePrint(" + JsonSerializer.Serialize(new { layout = options.PdfLayout.ToString() }) + ")", cancellationToken);
                await host.WaitReadyAsync(cancellationToken);
                var settings = host.View.CoreWebView2.Environment.CreatePrintSettings();
                settings.ShouldPrintBackgrounds = true;
                settings.ShouldPrintHeaderAndFooter = false;
                settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = 0;
                settings.ScaleFactor = 1;
                settings.PageWidth = (page?["width"]?.GetValue<double>() ?? throw new IOException("未返回 PDF 纸张宽度。")) / 96;
                settings.PageHeight = (page?["height"]?.GetValue<double>() ?? throw new IOException("未返回 PDF 纸张高度。")) / 96;
                using (var temporary = new NativeExportFile(".pdf"))
                {
                    // PrintToPdf cannot be aborted; always await completion before deleting its output.
                    var success = await host.View.CoreWebView2.PrintToPdfAsync(temporary.Path, settings);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!success || !File.Exists(temporary.Path) || new FileInfo(temporary.Path).Length < 100) throw new IOException("PDF 生成失败。");
                    var header = new byte[5];
                    using (var stream = File.OpenRead(temporary.Path)) await stream.ReadExactlyAsync(header, cancellationToken);
                    if (Encoding.ASCII.GetString(header) != "%PDF-") throw new IOException("生成文件不是有效 PDF。");
                    await temporary.CommitAsync(targetPath, cancellationToken);
                }
                return [targetPath];
            }
            if (!double.IsFinite(options.ImageScale) || options.ImageScale <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), "图片比例必须为正数；1 表示 100%。");
            var core = host.View.CoreWebView2;
            host.View.ZoomFactor = 1;
            await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", "{\"width\":1024,\"height\":768,\"deviceScaleFactor\":1,\"mobile\":false}").WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            var transparent = options.Transparent && options.Format == ExportFormat.Png;
            await core.CallDevToolsProtocolMethodAsync("Emulation.setDefaultBackgroundColorOverride", transparent ? "{\"color\":{\"r\":0,\"g\":0,\"b\":0,\"a\":0}}" : "{\"color\":{\"r\":255,\"g\":255,\"b\":255,\"a\":1}}").WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            var metrics = await host.EvaluateAsync("window.wb.getExportMetrics(" + JsonSerializer.Serialize(new { scale = options.ImageScale }) + ")", cancellationToken);
            var width = checked((int)Math.Ceiling(metrics?["width"]?.GetValue<double>() ?? 0));
            var height = checked((int)Math.Ceiling(metrics?["height"]?.GetValue<double>() ?? 0));
            if (width is < 1 or > MaximumImageDimension || height is < 1 or > MaximumImageDimension)
                throw new IOException("输出图片尺寸无效；两轴必须不超过 20000 像素。");
            // Chromium sees only small clips. WIC consumes full-width scanline
            // bands, so even a 400-megapixel image needs no full-size bitmap and
            // produces exactly one output file with no external tile manifest.
            var bandStride = checked(width * 4);
            var band = new byte[checked(bandStride * Math.Min(CaptureBandHeight, height))];
            using (var temporaryImage = new NativeExportFile(options.Format == ExportFormat.Jpeg ? ".jpg" : ".png"))
            {
                using (var writer = new RasterImageWriter(temporaryImage.Path, width, height, options.Format == ExportFormat.Jpeg))
                {
                    for (var y = 0; y < height; y += CaptureBandHeight)
                    {
                        var rows = Math.Min(CaptureBandHeight, height - y);
                        for (var x = 0; x < width; x += CaptureTileWidth)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var tileWidth = Math.Min(CaptureTileWidth, width - x);
                            var tile = new { x, y, width = tileWidth, height = rows };
                            await host.EvaluateAsync("window.wb.prepareExport(" + JsonSerializer.Serialize(new { scale = options.ImageScale, transparent, tile }) + ")", cancellationToken);
                            await host.WaitReadyAsync(cancellationToken);
                            // Always capture PNG internally: JPEG is encoded once
                            // after stitching, avoiding lossy seams between clips.
                            var screenshot = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", JsonSerializer.Serialize(new
                            {
                                format = "png", fromSurface = true, captureBeyondViewport = true,
                                clip = new { x = 0, y = 0, width = tileWidth, height = rows, scale = 1 }
                            })).WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            CopyScreenshotToBand(screenshot, tileWidth, rows, band, bandStride, x);
                        }
                        writer.WriteRows(band, rows, cancellationToken);
                    }
                    writer.Complete(cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(temporaryImage.Path) || new FileInfo(temporaryImage.Path).Length < 32)
                    throw new IOException("Windows 图片编码器没有生成有效文件。");
                await temporaryImage.CommitAsync(targetPath, cancellationToken);
            }
            return [targetPath];
        }
        finally
        {
            try
            {
                if (host.View.CoreWebView2 is not null)
                {
                    await host.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}").WaitAsync(TimeSpan.FromSeconds(5));
                    await host.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setDefaultBackgroundColorOverride", "{}").WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch { /* A closing WebView must not hide the original export error. */ }
            gate.Release();
        }
    }

    private static void CopyScreenshotToBand(string screenshot, int width, int rows, byte[] band, int stride, int offsetX)
    {
        using var response = JsonDocument.Parse(screenshot);
        if (!response.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
            throw new IOException("绘图引擎没有返回图片数据。");
        var bytes = Convert.FromBase64String(data.GetString()!);
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        var source = decoder.Frames[0];
        if (source.PixelWidth != width || source.PixelHeight != rows) throw new IOException("内部图片实际尺寸与请求的画布区域不一致。");
        BitmapSource pixels = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        pixels.CopyPixels(new Int32Rect(0, 0, width, rows), band, stride, checked(offsetX * 4));
    }

    private static async Task WriteAtomicallyAsync(string targetPath, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, targetPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
