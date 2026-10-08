using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using WhiteboardExporter.App.Services;

namespace WhiteboardExporter.App;

public partial class MainWindow
{
    private static void RequireSeparateLibraryReport(string source, string report)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var destination = Path.GetFullPath(report);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (destination.Equals(root, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("验证输出目录必须位于待检查目录之外，以保持原目录只读。");
    }
    /// <summary>Read-only regression of existing exports. The report contains counts and error codes only.</summary>
    internal async Task<bool> VerifyLibraryAsync(string sourceDirectory, string reportDirectory, CancellationToken ct = default)
    {
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        var destination = Path.GetFullPath(reportDirectory);
        RequireSeparateLibraryReport(sourceRoot, destination);
        Directory.CreateDirectory(destination);
        var scan = await OpenLibraryAsync(sourceRoot, ct);
        if (scan.Entries.Count > 500) throw new InvalidOperationException("只读预览验证最多检查 500 张白板。");
        var files = scan.Entries.SelectMany(entry => entry.SourceCandidates.Concat(entry.ImagePaths).Concat(entry.MediaPaths.Values))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var before = new Dictionary<string, (string Hash, DateTime Modified)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files) before[path] = (await HashFileAsync(path, ct), File.GetLastWriteTimeUtc(path));
        var results = new JsonArray();
        var succeeded = true;
        foreach (var item in boards.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            var result = new JsonObject(); results.Add(result);
            var errors = new JsonArray(); result["errors"] = errors;
            async Task CheckAsync(string format, Func<Task> action)
            {
                try { await action(); result[format] = true; }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    succeeded = false; result[format] = false;
                    errors.Add(new JsonObject { ["format"] = format, ["errorType"] = exception.GetType().Name, ["hresult"] = "0x" + exception.HResult.ToString("X8") });
                }
            }
            SelectQuietly(item);
            await CheckAsync("initialPreview", () => PreviewBoardAsync(item, ct));
            if (item.Document is { } document)
            {
                result["elementCount"] = document.ElementCount;
                result["contentStatus"] = document.Status;
                await CheckAsync("rawPreview", async () =>
                {
                    await ShowDocumentAsync(document, ct);
                    var id = (await sceneHost.EvaluateAsync("window.wb.getScene().board.id", ct))?.GetValue<string>();
                    if (id != document.Id) throw new InvalidDataException("原始预览身份不一致。");
                });
            }
            if (item.PdfPath is { } pdf)
                await CheckAsync("pdfPreview", async () =>
                {
                    await ShowPdfAsync(pdf);
                    if (!new Uri(PdfView.CoreWebView2.Source).LocalPath.Equals(pdf, StringComparison.OrdinalIgnoreCase)) throw new IOException("PDF 预览未打开目标文件。");
                });
            foreach (var image in item.LibraryEntry!.ImagePaths)
                await CheckAsync(Path.GetExtension(image).ToLowerInvariant() + "Preview", () =>
                {
                    ShowImage(image);
                    if (ImagePreview.Source is not BitmapSource) throw new IOException("图片预览没有像素。");
                    return Task.CompletedTask;
                });
            if (item.HtmlPath is { } html) await CheckAsync("htmlPreview", () => ShowOfflineVisualAsync(item, html, ct));
            if (item.SvgPath is { } svg) await CheckAsync("svgPreview", () => ShowOfflineVisualAsync(item, svg, ct));
            result["success"] = errors.Count == 0;
        }
        var unchanged = true;
        foreach (var pair in before)
            unchanged &= File.Exists(pair.Key) && File.GetLastWriteTimeUtc(pair.Key) == pair.Value.Modified && await HashFileAsync(pair.Key, ct) == pair.Value.Hash;
        var report = new JsonObject
        {
            ["success"] = succeeded && unchanged, ["readOnly"] = true, ["listedBoards"] = scan.Entries.Count,
            ["scanIssues"] = scan.Issues.Count, ["checkedFiles"] = files.Length, ["originalFilesUnchanged"] = unchanged, ["boards"] = results
        };
        await File.WriteAllTextAsync(Path.Combine(destination, "library-result.json"), report.ToJsonString(LocalWebHost.JsonOptions), ct);
        return succeeded && unchanged;
    }
}
