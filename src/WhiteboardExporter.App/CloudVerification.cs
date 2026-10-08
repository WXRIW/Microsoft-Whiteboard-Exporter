using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WhiteboardExporter.App.Cloud;
using WhiteboardExporter.App.Services;
using WhiteboardExporter.Core;

namespace WhiteboardExporter.App;

/// <summary>Authorized read-only cloud regression. Reports contain hashes and counts, never titles, URLs or response bodies.</summary>
internal static class CloudVerification
{
    public static async Task<bool> RunAsync(MainWindow window, string manifestPath, string directory, CancellationToken cancellationToken = default)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var ids = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(manifestPath, cancellationToken)) ?? [];
        if (ids.Length is 0 or > 100 || ids.Any(id => string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out _)) ||
            ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length)
            throw new InvalidDataException("白板清单须为包含 1 至 100 个不同白板 UUID 的 JSON 数组。");
        var results = new JsonArray();
        var report = new JsonObject { ["success"] = false, ["requestedBoards"] = ids.Length, ["boards"] = results };
        CloudWhiteboardService cloud;
        IReadOnlyList<CloudBoard> listed;
        try
        {
            cloud = await window.GetCloudServiceAsync();
            listed = await cloud.RefreshBoardsAsync(cancellationToken);
            report["listedBoards"] = listed.Count;
        }
        catch (Exception exception)
        {
            report["failedStage"] = "gallery";
            report["errorType"] = exception.GetType().Name;
            await RecordCloudFailureAsync(window, directory, report, exception, "gallery-preview.png");
            await SaveReportAsync(directory, report);
            return false;
        }
        foreach (var id in ids)
        {
            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16];
            var result = new JsonObject { ["boardKey"] = key, ["success"] = false };
            results.Add(result);
            var stage = "lookup";
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var board = listed.FirstOrDefault(board => string.Equals(board.Id, id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("请求的白板未出现在已登录账号的列表中。");
                var folder = Path.Combine(directory, key);
                Directory.CreateDirectory(folder);
                stage = "capture";
                // Showing the login tab ensures the editor can hydrate even in the hidden test window.
                await window.GetCloudServiceAsync();
                CloudCapture capture;
                try { capture = await cloud.CaptureAsync(board, Path.Combine(folder, "source"), cancellationToken); }
                catch (CloudCaptureIncompleteException exception)
                { capture = exception.Capture; result["captureIncomplete"] = true; }
                result["sourceResponses"] = capture.SourceResponses.Count;
                result["capturedImages"] = capture.Resources?.Count ?? 0;
                stage = "import";
                var document = await window.Importer.ImportAsync(capture.ZipPath, cancellationToken);
                document.Scene["board"]!["id"] = board.Id;
                document.Scene["board"]!["title"] = board.Title;
                BoardMetadata.ApplyCloudMetadata(document, capture.Modified ?? board.Modified,
                    capture.Modified is not null ? capture.ModifiedSource : board.ModifiedSource, DateTimeOffset.UtcNow);
                result["lastModifiedKnown"] = document.LastModifiedUtc is not null;
                foreach (var response in capture.SourceResponses)
                    document.Originals["responses/" + Path.GetFileName(response.Key)] = response.Value;
                stage = "resources";
                ResourceRecoveryService.ValidateLocalAssets(document);
                result["capturedResourcesApplied"] = capture.Resources is null ? 0 : ResourceRecoveryService.ApplyCapturedAssets(document, capture.Resources);
                result["liveResourcesRecovered"] = await cloud.ResolveAssetsAsync(document, cancellationToken);
                result["publicResourcesRecovered"] = await window.RecoverResourcesAsync(document, cancellationToken);
                result["contentStatus"] = document.Status;
                result["elementCount"] = document.ElementCount;
                result["assetCount"] = document.Assets.Count;
                result["missingAssets"] = document.Scene["diagnostics"]!["missingAssets"]!.AsArray().Count;
                result["diagnostics"] = new JsonArray(document.Scene["diagnostics"]!["items"]!.AsArray()
                    .Select(item => (JsonNode?)JsonValue.Create(item?["code"]?.GetValue<string>())).ToArray());
                stage = "redraw";
                await window.ShowDocumentAsync(document, cancellationToken);
                await window.ExportHost.LoadAsync(document, cancellationToken);
                var metrics = await window.ExportHost.EvaluateAsync("window.wb.getExportMetrics()", cancellationToken);
                var expectedWidth = checked((int)metrics!["width"]!.GetValue<double>());
                var expectedHeight = checked((int)metrics["height"]!.GetValue<double>());
                var formats = new JsonObject(); result["formats"] = formats;
                var allFormats = true;
                foreach (var format in Enum.GetValues<ExportFormat>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var formatResult = new JsonObject { ["status"] = "failed" }; formats[format.ToString()] = formatResult;
                    var extension = format switch { ExportFormat.Archive => "wbarchive", ExportFormat.Json => "json", ExportFormat.Pdf => "pdf", ExportFormat.Png => "png", ExportFormat.Jpeg => "jpg", ExportFormat.Svg => "svg", _ => "html" };
                    var path = Path.Combine(folder, "board." + extension);
                    try
                    {
                        var outputs = await window.Exporter.ExportAsync(document, new ExportOptions(format), path, cancellationToken);
                        await ValidateOutputAsync(window, document, format, path, expectedWidth, expectedHeight, cancellationToken);
                        formatResult["files"] = outputs.Count;
                        formatResult["bytes"] = new FileInfo(path).Length;
                        if (format == ExportFormat.Pdf)
                        {
                            await window.ShowPdfAsync(path);
                            await Task.Delay(1600, cancellationToken);
                            var view = (WebView2)window.FindName("PdfView");
                            if (view.CoreWebView2.Source != new Uri(path).AbsoluteUri) throw new InvalidDataException("PDF 预览来源不一致。");
                            var preview = Path.Combine(folder, "pdf-preview.png");
                            using (var stream = File.Create(preview)) await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                            Require(ReadPngDimensions(preview).Width > 0, "PDF 预览没有有效图像。");
                            result["pdfPreview"] = "complete";
                        }
                        if (format == ExportFormat.Png)
                        {
                            window.ShowImage(path);
                            Require(((Image)window.FindName("ImagePreview")).Source is BitmapSource image &&
                                image.PixelWidth > 0 && image.PixelHeight > 0 && Math.Max(image.PixelWidth, image.PixelHeight) <= 4096, "图片预览尺寸无效。");
                            result["imagePreview"] = "complete";
                        }
                        formatResult["status"] = "complete";
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    { allFormats = false; formatResult["errorType"] = exception.GetType().Name; }
                }
                result["success"] = document.Status == "complete" && allFormats &&
                    result["pdfPreview"]?.GetValue<string>() == "complete" && result["imagePreview"]?.GetValue<string>() == "complete";
            }
            catch (Exception exception)
            {
                result["failedStage"] = stage; result["errorType"] = exception.GetType().Name;
                await RecordCloudFailureAsync(window, Path.Combine(directory, key), result, exception, "cloud-error-preview.png");
            }
            report["success"] = results.Count == ids.Length && results.All(item => item?["success"]?.GetValue<bool>() == true);
            await SaveReportAsync(directory, report);
            if (cancellationToken.IsCancellationRequested) break;
        }
        return report["success"]!.GetValue<bool>();
    }

    private static async Task ValidateOutputAsync(MainWindow window, BoardDocument source, ExportFormat format, string path, int width, int height, CancellationToken cancellationToken)
    {
        Require(File.Exists(path) && new FileInfo(path).Length > 20, "导出文件为空。");
        if (format is ExportFormat.Archive or ExportFormat.Json)
        {
            var restored = await window.Importer.ImportAsync(path, cancellationToken);
            Require(restored.Id == source.Id && restored.ElementCount == source.ElementCount && restored.Status == source.Status, "数据往返后身份、元素或状态不一致。");
            foreach (var asset in ((JsonObject)source.Scene["assets"]!).Select(entry => entry.Value).OfType<JsonObject>())
                if (asset["path"]?.GetValue<string>() is { } relative && source.Assets.TryGetValue(relative, out var bytes))
                    Require(restored.Assets.TryGetValue(relative, out var actual) && bytes.AsSpan().SequenceEqual(actual), "数据往返后图片字节不一致。");
            if (format == ExportFormat.Archive)
                foreach (var original in source.Originals)
                    Require(restored.Originals.TryGetValue(original.Key, out var bytes) && original.Value.AsSpan().SequenceEqual(bytes), "原始数据备份字节不一致。");
        }
        else if (format == ExportFormat.Png) Require(ReadPngDimensions(path) == (width, height), "PNG 尺寸与完整画板不一致。");
        else if (format == ExportFormat.Jpeg)
        {
            var header = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            Require(header.PixelWidth == width && header.PixelHeight == height, "JPEG 尺寸与完整画板不一致。");
        }
        else if (format == ExportFormat.Pdf)
        {
            using var stream = File.OpenRead(path); var header = new byte[5]; await stream.ReadExactlyAsync(header, cancellationToken);
            Require(header.AsSpan().SequenceEqual("%PDF-"u8), "PDF 文件头无效。");
        }
        else if (format == ExportFormat.Svg) Require(XDocument.Load(path).Root?.Name.LocalName == "svg", "SVG 文档无效。");
        else
        {
            var html = await File.ReadAllTextAsync(path, cancellationToken);
            Require(html.Contains("<svg", StringComparison.OrdinalIgnoreCase) && !html.Contains("<script", StringComparison.OrdinalIgnoreCase), "HTML 不含独立画布或含脚本。");
        }
    }

    private static (int Width, int Height) ReadPngDimensions(string path)
    {
        using var stream = File.OpenRead(path); Span<byte> header = stackalloc byte[24]; stream.ReadExactly(header);
        Require(header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && header[12..16].SequenceEqual("IHDR"u8), "PNG 文件头无效。");
        return (BinaryPrimitives.ReadInt32BigEndian(header[16..20]), BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
    }

    private static async Task SaveReportAsync(string directory, JsonObject report)
    {
        var path = Path.Combine(directory, "cloud-result.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, report.ToJsonString(LocalWebHost.JsonOptions)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task RecordCloudFailureAsync(MainWindow window, string directory, JsonObject result, Exception exception, string imageName)
    {
        var chain = new JsonArray();
        for (Exception? current = exception; current is not null && chain.Count < 8; current = current.InnerException)
            chain.Add(new JsonObject { ["type"] = current.GetType().Name, ["hresult"] = $"0x{current.HResult:X8}", ["code"] = FailureCode(current) });
        result["errorChain"] = chain;
        // The screenshot stays in the private output directory. URL query,
        // account details and copied response text never enter the report.
        try
        {
            var view = (WebView2)window.FindName("CloudView");
            result["browserInitialized"] = view.CoreWebView2 is not null;
            if (view.CoreWebView2 is not { } core) return;
            if (Uri.TryCreate(core.Source, UriKind.Absolute, out var uri)) result["pageHost"] = uri.Host;
            Directory.CreateDirectory(directory);
            using var stream = File.Create(Path.Combine(directory, imageName));
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            result["diagnosticPreview"] = "complete";
        }
        catch (Exception previewException) { result["diagnosticPreview"] = "failed"; result["diagnosticPreviewErrorType"] = previewException.GetType().Name; }
    }

    private static string FailureCode(Exception exception)
    {
        // Classify only fixed application error wording, never echo the message.
        var message = exception.Message;
        if (exception.GetType().Name == "LoginRequiredException") return "LoginInteractionRequired";
        if (message.StartsWith("无法初始化 WebView2", StringComparison.Ordinal)) return "WebViewInitFailed";
        if (message.StartsWith("微软登录尚未完成", StringComparison.Ordinal)) return "LoginExpired";
        if (message.StartsWith("微软白板显示了服务错误页", StringComparison.Ordinal)) return "ServiceError";
        if (message.StartsWith("微软白板列表在读取过程中消失", StringComparison.Ordinal)) return "GalleryDisappeared";
        if (message.StartsWith("微软页面加载失败", StringComparison.Ordinal)) return "PageNavigationFailed";
        if (message.StartsWith("读取虚拟白板列表超时", StringComparison.Ordinal)) return "GalleryTimeout";
        if (exception is OperationCanceledException) return "Cancelled";
        if (exception is TimeoutException) return "PageTimeout";
        if (exception is UnauthorizedAccessException) return "ProfileAccessDenied";
        if (exception is System.Runtime.InteropServices.COMException) return "BrowserComError";
        return "CloudOperationFailed";
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
