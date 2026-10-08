using System.IO;
using System.Text;
using System.Text.Json;
using WhiteboardExporter.Core;

namespace WhiteboardExporter.App.Services;

public sealed record OfflineMediaPreviewResult(string Title, double Width, double Height, int BlockedResources, int RemovedActiveContent);

/// <summary>Only parses a saved visual; it does not convert it into original board data.</summary>
public static class OfflineMediaPreviewService
{
    private const long MaximumMediaBytes = 128L * 1024 * 1024;
    public static async Task<OfflineMediaPreviewResult> LoadAsync(LocalWebHost host, string path, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".html" or ".htm" or ".svg")) throw new InvalidDataException("媒体预览支持离线 HTML 或 SVG。");
        var markup = await ReadMarkupAsync(path, cancellationToken);
        var input = JsonSerializer.Serialize(new
        {
            markup,
            format = extension == ".svg" ? "svg" : "html",
            title = Path.GetFileNameWithoutExtension(path)
        });
        host.View.ZoomFactor = 1;
        var result = await host.EvaluateAsync("window.wb.loadOfflineSvg(" + input + ")", cancellationToken)
            ?? throw new InvalidDataException("离线媒体预览未返回画板信息。");
        await host.WaitReadyAsync(cancellationToken);
        return new OfflineMediaPreviewResult(
            result["title"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(path),
            result["width"]?.GetValue<double>() ?? 0,
            result["height"]?.GetValue<double>() ?? 0,
            result["blockedResources"]?.GetValue<int>() ?? 0,
            result["removedActiveContent"]?.GetValue<int>() ?? 0);
    }

    private static async Task<string> ReadMarkupAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumMediaBytes) throw new InvalidDataException("离线媒体超过 128 MiB 限制。");
        using var contents = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (contents.Length + count > MaximumMediaBytes) throw new InvalidDataException("离线媒体超过 128 MiB 限制。");
            await contents.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        try { return new UTF8Encoding(false, true).GetString(contents.GetBuffer(), 0, checked((int)contents.Length)).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException exception) { throw new InvalidDataException("离线媒体文件不是有效 UTF-8 文本。", exception); }
    }
}
