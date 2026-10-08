using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Microsoft.Web.WebView2.Core;
using WhiteboardExporter.App.Services;
using WhiteboardExporter.Core;

namespace WhiteboardExporter.App;

internal static class SmokeTests
{
    public static async Task<bool> RunAsync(MainWindow window, string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        try
        {
            if (window.PdfPreviewOnly)
            {
                var path = Path.Combine(directory, "markers.pdf");
                await window.Exporter.ExportAsync(MakeMarkers(), new ExportOptions(ExportFormat.Pdf), path);
                if (window.PdfPreviewSmall) window.ChangeTestViewport();
                await window.ImportPathAsync(path);
                await window.ImportPathAsync(path);
                await Task.Delay(1500);
                await PdfPreviewService.SaveDiagnosticsAsync(GetPdfCore(window), Path.Combine(directory, "pdf-reader-diagnostics.json"));
                using (var stream = File.Create(Path.Combine(directory, "pdf-preview.png"))) await GetPdfCore(window).CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                Check(HasMarkerColors(Path.Combine(directory, "pdf-preview.png")), "PDF 默认适应整页", checks);
                await File.WriteAllTextAsync(Path.Combine(directory, "smoke-result.json"), JsonSerializer.Serialize(new { success = true, checks }, LocalWebHost.JsonOptions));
                return true;
            }
            Check(window.LibraryDefaultsChecked(), "离线 HTML 与 PNG 透明背景默认勾选", checks);
            foreach (var resourceCheck in await ResourceRecoveryTests.RunAsync(Path.Combine(directory, "resource-tests")))
                Check(true, resourceCheck, checks);
            var samples = Path.Combine(AppContext.BaseDirectory, "samples");
            var demo = await window.Importer.ImportAsync(Path.Combine(samples, "demo-scene.json"));
            Check(demo.ElementCount > 2, "本地 JSON 与资源导入", checks);
            var htmlPath = Path.Combine(samples, "microsoft-export.html");
            if (!File.Exists(htmlPath)) throw new IOException("缺少微软格式 HTML 测试样本。");
            var zipPath = Path.Combine(directory, "microsoft-input.zip");
            using (var zipStream = File.Create(zipPath))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(htmlPath, "board.html");
                var commentFile = Path.Combine(samples, "microsoft-export-comments.json");
                if (File.Exists(commentFile)) archive.CreateEntryFromFile(commentFile, "board-comments.json");
                var assetFolder = Path.Combine(samples, "assets");
                if (Directory.Exists(assetFolder)) foreach (var path in Directory.EnumerateFiles(assetFolder)) archive.CreateEntryFromFile(path, "assets/" + Path.GetFileName(path));
            }
            var normalized = await window.Importer.ImportAsync(zipPath);
            await MainWindow.TestRetrySourceAsync(zipPath, Path.Combine(directory, "source-resume"));
            Check(true, "旧失败任务找到本地源，拒绝源哈希变化、越界路径和云端新版本", checks);
            Check(normalized.ElementCount > 2, "微软 HTML+评论 ZIP 经真实 DOMParser 解析", checks);
            Check(normalized.Originals.Values.Any(bytes => bytes.AsSpan().SequenceEqual(File.ReadAllBytes(zipPath))), "原始 ZIP 字节完整保留", checks);
            var emptyZip = Path.Combine(directory, "empty-input.zip");
            using (var stream = File.Create(emptyZip))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                zip.CreateEntryFromFile(Path.Combine(samples, "empty-export.html"), "Whiteboard.html");
            var empty = await window.Importer.ImportAsync(emptyZip);
            Check(empty.Status == "complete" && empty.ElementCount == 0 && empty.Scene["diagnostics"]!["items"]!.AsArray().Any(d => d?["code"]?.GetValue<string>() == "EMPTY_CANVAS"), "合法官方空画布明确标记且可重绘", checks);
            var staleEmpty = new BoardDocument { Scene = (JsonObject)empty.Scene.DeepClone() };
            staleEmpty.Scene["diagnostics"]!["status"] = "failed";
            staleEmpty.Scene["diagnostics"]!["items"] = new JsonArray(JsonNode.Parse("""{"code":"NO_WHITEBOARD_ANCHORS","severity":"error","message":"旧版未识别空画布"}"""));
            foreach (var pair in empty.Originals) staleEmpty.Originals[pair.Key] = pair.Value;
            var stalePath = Path.Combine(directory, "old-failed-empty.wbarchive");
            await ArchiveService.SaveAsync(staleEmpty, stalePath);
            var recoveredEmpty = await window.Importer.ImportAsync(stalePath);
            Check(recoveredEmpty.Status == "complete" && recoveredEmpty.ElementCount == 0 && recoveredEmpty.Originals.Values.Any(bytes => bytes.AsSpan().SequenceEqual(File.ReadAllBytes(emptyZip))), "真实 DOMParser 从旧失败备份恢复空画布且保留原 ZIP", checks);
            await window.Exporter.ExportAsync(recoveredEmpty, new ExportOptions(ExportFormat.Pdf), Path.Combine(directory, "empty.pdf"));
            await window.Exporter.ExportAsync(recoveredEmpty, new ExportOptions(ExportFormat.Png), Path.Combine(directory, "empty.png"));
            Check(File.Exists(Path.Combine(directory, "empty.pdf")) && File.Exists(Path.Combine(directory, "empty.png")), "原先失败的空画布可导出单页 PDF 和原尺寸图片", checks);
            await window.Exporter.ExportAsync(normalized, new ExportOptions(ExportFormat.Archive), Path.Combine(directory, "imported.wbarchive"));
            var replay = await window.Importer.ImportAsync(Path.Combine(directory, "imported.wbarchive"));
            Check(replay.ElementCount == normalized.ElementCount, "备份包重新导入", checks);
            await window.ShowDocumentAsync(replay);
            await window.Exporter.ExportAsync(replay, new ExportOptions(ExportFormat.Png, .5), Path.Combine(directory, "imported-redraw.png"));
            var jsonPath = Path.Combine(directory, "json-only", "scene.json");
            await window.Exporter.ExportAsync(replay, new ExportOptions(ExportFormat.Json), jsonPath);
            var jsonOnly = await window.Importer.ImportAsync(jsonPath);
            Check(jsonOnly.Assets.Count > 0 && jsonOnly.ElementCount == replay.ElementCount, "仅 JSON 与本地资源恢复完整画布", checks);
            var redrawPdf = Path.Combine(directory, "json-only", "redrawn.pdf");
            var redrawPng = Path.Combine(directory, "json-only", "redrawn.png");
            await window.Exporter.ExportAsync(jsonOnly, new ExportOptions(ExportFormat.Pdf), redrawPdf);
            await window.Exporter.ExportAsync(jsonOnly, new ExportOptions(ExportFormat.Png, .5), redrawPng);
            File.Delete(redrawPdf); File.Delete(redrawPng);
            var freshJsonOnly = await window.Importer.ImportAsync(jsonPath);
            await window.Exporter.ExportAsync(freshJsonOnly, new ExportOptions(ExportFormat.Pdf), redrawPdf);
            await window.Exporter.ExportAsync(freshJsonOnly, new ExportOptions(ExportFormat.Png, .5), redrawPng);
            Check(File.Exists(redrawPdf) && File.Exists(redrawPng), "删除已有 PDF/PNG 后由 JSON 再次导出（离线页面禁止联网）", checks);
            checks.Add(new { name = "真实账号验收", status = "pending", detail = "未自动登录用户账号；云端列表、官方 ZIP 控件需用户在微软页面完成登录后验证。" });

            var markers = MakeMarkers();
            AssetService.ExtractEmbeddedAssets(markers);
            await window.SceneHost.LoadAsync(markers);
            foreach (var format in Enum.GetValues<ExportFormat>())
            {
                var extension = format switch { ExportFormat.Archive => "wbarchive", ExportFormat.Json => "json", ExportFormat.Pdf => "pdf", ExportFormat.Png => "png", ExportFormat.Jpeg => "jpg", ExportFormat.Svg => "svg", _ => "html" };
                var path = Path.Combine(directory, "markers." + extension);
                await window.Exporter.ExportAsync(markers, new ExportOptions(format), path);
                Check(File.Exists(path) && new FileInfo(path).Length > 20, "导出 " + format, checks);
                if (format == ExportFormat.Svg) XDocument.Parse(await File.ReadAllTextAsync(path));
            }
            Pixels(Path.Combine(directory, "markers.png"), out var nativeWidth, out var nativeHeight);
            Check(nativeWidth == 2400 && nativeHeight == 1800, "默认图片按原画板 2400 × 1800 像素输出", checks);
            Check(HasMarkerColors(Path.Combine(directory, "markers.png")), "完整画布四角及负坐标标记均保留", checks);
            window.ChangeTestViewport();
            await window.SceneHost.EvaluateAsync("window.wb.setZoom(0.2)");
            await window.Exporter.ExportAsync(markers, new ExportOptions(ExportFormat.Png), Path.Combine(directory, "markers-after-zoom.png"));
            Check(RasterComparison.Equivalent(Path.Combine(directory, "markers.png"), Path.Combine(directory, "markers-after-zoom.png"), .00001), "窗口与预览缩放不改变导出尺寸及内容（容许次像素抗锯齿差异）", checks);
            await window.Exporter.ExportAsync(markers, new ExportOptions(ExportFormat.Png, 2), Path.Combine(directory, "markers-200.png"));
            ReadDimensions(Path.Combine(directory, "markers-200.png"), out var doubledWidth, out var doubledHeight);
            Check(doubledWidth == 4800 && doubledHeight == 3600 && HasMarkerColors(Path.Combine(directory, "markers-200.png")), "200% 输出 4800 × 3600 单图并保留四角", checks);
            var seams = MakeWideMarkers(4800, 900);
            seams.Scene["elements"]!.AsArray().Add(JsonNode.Parse("""{"id":"seam-content","kind":"shape","z":8,"transform":[1,0,0,1,-100,-100],"bounds":{"x":0,"y":0,"width":4800,"height":900},"svg":"<defs><linearGradient id='seam-gradient'><stop offset='0' stop-color='#a2dafe'/><stop offset='1' stop-color='#cd316b'/></linearGradient></defs><path d='M 3900 400 L 4300 650 L 4700 480' stroke='url(#seam-gradient)' stroke-width='43' fill='none'/><ellipse cx='4096' cy='512' rx='150' ry='140' fill='#845ab2' opacity='.51'/>"}"""));
            var stitchedPath = Path.Combine(directory, "seams.png");
            await window.Exporter.ExportAsync(seams, new ExportOptions(ExportFormat.Png), stitchedPath);
            await window.ExportHost.LoadAsync(seams);
            await window.ExportHost.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", "{\"width\":1024,\"height\":768,\"deviceScaleFactor\":1,\"mobile\":false}");
            await window.ExportHost.EvaluateAsync("window.wb.prepareExport({scale:1})");
            await window.ExportHost.WaitReadyAsync();
            var referenceCapture = await window.ExportHost.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{\"format\":\"png\",\"fromSurface\":true,\"captureBeyondViewport\":true,\"clip\":{\"x\":0,\"y\":0,\"width\":4800,\"height\":900,\"scale\":1}}");
            var referencePath = Path.Combine(directory, "seams-reference.png");
            using (var referenceJson = JsonDocument.Parse(referenceCapture)) await File.WriteAllBytesAsync(referencePath, Convert.FromBase64String(referenceJson.RootElement.GetProperty("data").GetString()!));
            await window.ExportHost.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}");
            Check(RasterComparison.Equivalent(stitchedPath, referencePath), "跨 4096／512 边界无缺口或位移（容许次像素抗锯齿差异）", checks);
            Check(RasterComparison.VerifyRejectsGapsAndShifts(stitchedPath, referencePath), "接缝验证拒绝一像素空行、空列及横纵错位", checks);
            await window.Exporter.ExportAsync(markers, new ExportOptions(ExportFormat.Png, .5), Path.Combine(directory, "markers-50.png"));
            ReadDimensions(Path.Combine(directory, "markers-50.png"), out var halfWidth, out var halfHeight);
            Check(halfWidth == 1200 && halfHeight == 900, "50% 按原画板等比缩小", checks);
            await window.Exporter.ExportAsync(markers, new ExportOptions(ExportFormat.Pdf, PdfLayout: PdfLayout.A4Fit), Path.Combine(directory, "markers-a4.pdf"));
            await window.Exporter.ExportAsync(markers, new ExportOptions(ExportFormat.Pdf, PdfLayout: PdfLayout.A3Fit), Path.Combine(directory, "markers-a3.pdf"));
            await window.Exporter.ExportAsync(markers, new ExportOptions(ExportFormat.Pdf, PdfLayout: PdfLayout.A4Tiles), Path.Combine(directory, "markers-pages.pdf"));
            Check(File.ReadAllBytes(Path.Combine(directory, "markers-pages.pdf")).Length > 1000, "PDF A3 与 A4 分页生成", checks);

            var large = MakeWideMarkers(9000, 300);
            await window.TestBatchRecoveryAsync(markers, Path.Combine(directory, "batch-recovery"));
            Check(true, "单图片格式写入失败仍完成 HTML/备份/PDF，解除阻碍后重试恢复", checks);
            await window.TestBoardIdentityAsync(markers, Path.Combine(directory, "board-identity"));
            Check(true, "同名白板选择与预览同步、清除示例、重复导出及重命名复用、同路径媒体刷新", checks);
            foreach (var libraryCheck in await window.TestLibraryAsync(markers, Path.Combine(directory, "library-browser")))
                Check(true, libraryCheck, checks);
            foreach (var accountCheck in await window.TestAccountIsolationAsync(markers, Path.Combine(directory, "account-isolation")))
                Check(true, accountCheck, checks);
            foreach (var bindingCheck in await window.TestCloudAccountBindingAsync(markers, Path.Combine(directory, "cloud-account-binding")))
                Check(true, bindingCheck, checks);
            var nativeLargePath = Path.Combine(directory, "native-9000.png");
            await window.Exporter.ExportAsync(large, new ExportOptions(ExportFormat.Png), nativeLargePath);
            Pixels(nativeLargePath, out var largeWidth, out var largeHeight);
            Check(largeWidth == 9000 && largeHeight == 300 && HasMarkerColors(nativeLargePath), "原尺寸超过 8192 仍不缩小或裁切", checks);
            var wide = MakeWideMarkers(20000, 800);
            var wideFiles = await window.Exporter.ExportAsync(wide, new ExportOptions(ExportFormat.Png), Path.Combine(directory, "wide.png"));
            ReadDimensions(Path.Combine(directory, "wide.png"), out var wideWidth, out var wideHeight);
            Check(wideFiles.Count == 1 && wideWidth == 20000 && wideHeight == 800 && HasMarkerColors(Path.Combine(directory, "wide.png")), "20000 × 800 输出单图且完整保留四角", checks);
            await window.Exporter.ExportAsync(wide, new ExportOptions(ExportFormat.Jpeg), Path.Combine(directory, "wide.jpg"));
            ReadDimensions(Path.Combine(directory, "wide.jpg"), out var jpegWidth, out var jpegHeight);
            Check(jpegWidth == 20000 && jpegHeight == 800 && HasMarkerColors(Path.Combine(directory, "wide.jpg")), "同名 JPEG 也是单图，无外部分块或拼接清单", checks);
            var overLimit = MakeWideMarkers(40000, 1600);
            await window.Exporter.ExportAsync(overLimit, new ExportOptions(ExportFormat.Png, 2), Path.Combine(directory, "limited.png"));
            ReadDimensions(Path.Combine(directory, "limited.png"), out var limitedWidth, out var limitedHeight);
            Check(limitedWidth == 20000 && limitedHeight == 800 && HasMarkerColors(Path.Combine(directory, "limited.png")), "40000 × 1600 请求 200% 自动等比限到 20000 × 800", checks);
            var square = MakeWideMarkers(20000, 20000);
            await window.Exporter.ExportAsync(square, new ExportOptions(ExportFormat.Png), Path.Combine(directory, "square-20000.png"));
            ReadDimensions(Path.Combine(directory, "square-20000.png"), out var squareWidth, out var squareHeight);
            Check(squareWidth == 20000 && squareHeight == 20000 && HasMarkerColors(Path.Combine(directory, "square-20000.png")), "20000 × 20000 单张 PNG 不裁切、不额外缩小", checks);
            await window.Exporter.ExportAsync(square, new ExportOptions(ExportFormat.Jpeg), Path.Combine(directory, "square-20000.jpg"));
            ReadDimensions(Path.Combine(directory, "square-20000.jpg"), out squareWidth, out squareHeight);
            Check(squareWidth == 20000 && squareHeight == 20000 && HasMarkerColors(Path.Combine(directory, "square-20000.jpg")), "20000 × 20000 单张 JPEG 不裁切、不额外缩小", checks);
            window.ShowImage(Path.Combine(directory, "square-20000.png"));
            Check(((System.Windows.Controls.Image)window.FindName("ImagePreview")).Source is BitmapSource largePreview && largePreview.PixelWidth == 4096 && largePreview.PixelHeight == 4096 && ((System.Windows.Controls.TextBlock)window.FindName("ImageDetails")).Text.Contains("20000 × 20000"), "最大图片在应用中预览并显示真实尺寸，预览位图限制 4096", checks);
            Check(!Directory.GetFiles(directory, "*-r*-c*.png").Any() && !Directory.GetFiles(directory, "*-tiles.json").Any(), "图片仅输出所选格式的完整文件", checks);
            await window.Exporter.ExportAsync(wide, new ExportOptions(ExportFormat.Pdf), Path.Combine(directory, "wide-canvas.pdf"));
            Check(File.ReadAllBytes(Path.Combine(directory, "wide-canvas.pdf")).Length > 1000, "超大画板仍生成完整动态纸张单页 PDF", checks);

            var transparentScene = new BoardDocument { Scene = (JsonObject)markers.Scene.DeepClone() };
            await window.Exporter.ExportAsync(transparentScene, new ExportOptions(ExportFormat.Png, .5, Transparent: true), Path.Combine(directory, "transparent.png"));
            Check(HasTransparency(Path.Combine(directory, "transparent.png")), "透明 PNG 保留 alpha", checks);
            var malicious = await window.ExportHost.NormalizeAsync("<html><body><div id='canvasContent'><div class='anchor' data-whiteboard-type='PlainText' style='left:0px;top:0px;width:200px;height:80px'><script>window.pwned=1</script><div onclick='window.pwned=1'><a href='https://example.com'>安全文本</a><img src='https://example.com/a.png' onerror='window.pwned=1'></div></div></div></body></html>", null, "security", "安全检查");
            var unsafeDocument = new BoardDocument { Scene = malicious };
            AssetService.ExtractEmbeddedAssets(unsafeDocument);
            await window.Exporter.ExportAsync(unsafeDocument, new ExportOptions(ExportFormat.Html), Path.Combine(directory, "sanitized.html"));
            var safeHtml = await File.ReadAllTextAsync(Path.Combine(directory, "sanitized.html"));
            Check(!safeHtml.Contains("onclick=", StringComparison.OrdinalIgnoreCase) && !safeHtml.Contains("onerror=", StringComparison.OrdinalIgnoreCase) && !safeHtml.Contains("<script", StringComparison.OrdinalIgnoreCase), "脚本和事件从离线渲染移除", checks);
            Check((await window.ExportHost.EvaluateAsync("typeof window.pwned"))?.GetValue<string>() == "undefined", "解析源 HTML 不执行脚本", checks);
            await window.ImportPathAsync(Path.Combine(directory, "markers.pdf"));
            await Task.Delay(1800);
            await PdfPreviewService.SaveDiagnosticsAsync(GetPdfCore(window), Path.Combine(directory, "pdf-reader-diagnostics.json"));
            using (var stream = File.Create(Path.Combine(directory, "pdf-preview.png"))) await GetPdfCore(window).CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            Check(HasMarkerColors(Path.Combine(directory, "pdf-preview.png")), "内嵌 PDF 预览可见完整画布", checks);
            await window.ImportPathAsync(Path.Combine(directory, "markers.png"));
            Check(((System.Windows.Controls.Image)window.FindName("ImagePreview")).Source is BitmapSource image && image.PixelWidth == 2400 && image.PixelHeight == 1800, "内嵌图片预览显示实际原尺寸图像", checks);
            Check(window.SceneHost.View.Visibility == System.Windows.Visibility.Hidden, "媒体文件不显示上一张白板的原始重绘", checks);
            var imageScale = (System.Windows.Media.ScaleTransform)window.FindName("ImageScale");
            var scroller = (System.Windows.Controls.ScrollViewer)window.FindName("ImageScroller");
            Check(imageScale.ScaleX < 1 && 2400 * imageScale.ScaleX <= scroller.ActualWidth && 1800 * imageScale.ScaleY <= scroller.ActualHeight, "图片适应预览窗口", checks);
            await window.ImportPathAsync(Path.Combine(directory, "imported.wbarchive"));
            Check(window.SceneHost.View.Visibility == System.Windows.Visibility.Visible && ((System.Windows.Controls.TabControl)window.FindName("PreviewTabs")).SelectedItem == window.FindName("SceneTab"), "从 PDF/图片切换回原始重绘", checks);
            using (var stream = File.Create(Path.Combine(directory, "scene-preview.png"))) await window.SceneHost.View.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            CaptureShell(window, Path.Combine(directory, "scene-preview.png"), Path.Combine(directory, "application-preview.png"));
            await File.WriteAllTextAsync(Path.Combine(directory, "smoke-result.json"), JsonSerializer.Serialize(new { success = true, utc = DateTimeOffset.UtcNow, platform = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), checks }, LocalWebHost.JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "smoke-result.json"), JsonSerializer.Serialize(new { success = false, utc = DateTimeOffset.UtcNow, error = ex.ToString(), checks }, LocalWebHost.JsonOptions));
            return false;
        }
    }
    private static CoreWebView2 GetPdfCore(MainWindow window) => ((Microsoft.Web.WebView2.Wpf.WebView2)window.FindName("PdfView")).CoreWebView2;
    private static void CaptureShell(MainWindow window, string scenePreview, string target)
    {
        window.UpdateLayout();
        var width = (int)Math.Ceiling(window.ActualWidth); var height = (int)Math.Ceiling(window.ActualHeight);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(window), null, new System.Windows.Rect(0, 0, width, height));
            var view = window.SceneHost.View;
            var bounds = view.TransformToAncestor(window).TransformBounds(new System.Windows.Rect(0, 0, view.ActualWidth, view.ActualHeight));
            using var stream = File.OpenRead(scenePreview);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            context.DrawImage(decoder.Frames[0], bounds);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(target); encoder.Save(output);
    }
    private static void Check(bool condition, string name, List<object> checks)
    { if (!condition) throw new InvalidOperationException("验收失败：" + name); checks.Add(new { name, status = "passed" }); }

    private static BoardDocument MakeMarkers()
    {
        var elements = new JsonArray();
        foreach (var (id, x, y, color) in new[] { ("red", -80, -80, "#ff0000"), ("green", 2200, -80, "#00ff00"), ("blue", -80, 1620, "#0000ff"), ("orange", 2200, 1620, "#ff8000") })
            elements.Add(JsonNode.Parse(JsonSerializer.Serialize(new { id, kind = "shape", z = 0, transform = new[] { 1, 0, 0, 1, x, y }, bounds = new { x = 0, y = 0, width = 60, height = 60 }, svg = $"<rect x=\"0\" y=\"0\" width=\"60\" height=\"60\" fill=\"{color}\"/>" })));
        elements.Add(JsonNode.Parse("""{"id":"chinese","kind":"text","z":1,"transform":[1,0,0,1,250,300],"bounds":{"x":0,"y":0,"width":1100,"height":130},"text":"离线重绘验收 · 中文文字、负坐标与完整画布","style":{"css":"font-size:48px;color:#172b4d"}}"""));
        elements.Add(JsonNode.Parse("""{"id":"nested","kind":"group","z":2,"transform":[0.9659258,0.258819,-0.258819,0.9659258,500,650],"bounds":{"x":0,"y":0,"width":700,"height":200},"children":[{"id":"ink","kind":"ink","z":0,"transform":[1,0,0,1,0,0],"bounds":{"x":0,"y":0,"width":650,"height":150},"svg":"<path d='M10 80 Q150 0 280 80 T620 100' fill='none' stroke='#1565c0' stroke-width='18'/><path d='M40 105 L580 105' stroke='#ffdd00' stroke-width='36' opacity='.4'/>"}]}"""));
        return new BoardDocument
        {
            Scene = new JsonObject
            {
                ["schemaVersion"] = 1, ["board"] = new JsonObject { ["id"] = "smoke-markers", ["title"] = "完整画布与中文验收" },
                ["bounds"] = new JsonObject { ["x"] = -100, ["y"] = -100, ["width"] = 2400, ["height"] = 1800 }, ["background"] = "#ffffff", ["elements"] = elements,
                ["assets"] = new JsonObject(), ["diagnostics"] = new JsonObject { ["status"] = "complete", ["total"] = 6, ["supported"] = 6, ["unsupported"] = 0, ["items"] = new JsonArray(), ["missingAssets"] = new JsonArray() }
            }
        };
    }
    private static BoardDocument MakeWideMarkers(int width, int height)
    {
        var document = MakeMarkers();
        document.Scene["board"] = new JsonObject { ["id"] = "wide-" + width, ["title"] = "离线重绘验收 · 原画板尺寸" };
        document.Scene["bounds"] = new JsonObject { ["x"] = -100, ["y"] = -100, ["width"] = width, ["height"] = height };
        var elements = document.Scene["elements"]!.AsArray();
        elements.RemoveAt(5); elements.RemoveAt(4);
        elements[1]!["transform"]![4] = width - 180;
        elements[2]!["transform"]![5] = height - 180;
        elements[3]!["transform"]![4] = width - 180;
        elements[3]!["transform"]![5] = height - 180;
        elements.Add(JsonNode.Parse("""{"id":"chinese","kind":"text","z":1,"transform":[1,0,0,1,250,0],"bounds":{"x":0,"y":0,"width":1100,"height":100},"text":"离线重绘验收 · 原画板尺寸","style":{"css":"font-size:48px;color:#172b4d"}}"""));
        document.Scene["diagnostics"]!["total"] = 5;
        document.Scene["diagnostics"]!["supported"] = 5;
        return document;
    }
    private static byte[] Pixels(string path, out int width, out int height)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        width = converted.PixelWidth; height = converted.PixelHeight;
        var bytes = new byte[width * height * 4]; converted.CopyPixels(bytes, width * 4, 0); return bytes;
    }
    private static bool HasMarkerColors(string path) => MarkerColorMask(path) == 15;
    private static int MarkerColorMask(string path)
    {
        var frame = BitmapFrame.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(Path.GetFullPath(path));
        if (Math.Max(frame.PixelWidth, frame.PixelHeight) > 2400)
        { if (frame.PixelWidth >= frame.PixelHeight) image.DecodePixelWidth = 2400; else image.DecodePixelHeight = 2400; }
        image.EndInit(); image.Freeze();
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(pixels, converted.PixelWidth * 4, 0); var mask = 0;
        for (var i = 0; i < pixels.Length; i += 4 * 3)
        {
            var b = pixels[i]; var g = pixels[i + 1]; var r = pixels[i + 2];
            if (r > 245 && g < 10 && b < 10) mask |= 1;
            if (g > 245 && r < 10 && b < 10) mask |= 2;
            if (b > 245 && r < 10 && g < 10) mask |= 4;
            if (r > 245 && g is > 115 and < 140 && b < 10) mask |= 8;
            if (mask == 15) return mask;
        }
        return mask;
    }
    private static bool HasTransparency(string path)
    {
        var pixels = Pixels(path, out var width, out var height);
        return pixels[((height / 2) * width + width / 2) * 4 + 3] < 255;
    }
    private static void ReadDimensions(string path, out int width, out int height)
    {
        var frame = BitmapFrame.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        width = frame.PixelWidth; height = frame.PixelHeight;
    }
}
