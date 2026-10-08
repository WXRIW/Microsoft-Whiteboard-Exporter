using System.Text.Json.Nodes;

namespace WhiteboardExporter.Core;

public sealed class BoardDocument
{
    public required JsonObject Scene { get; init; }
    public Dictionary<string, byte[]> Assets { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, byte[]> Originals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? SourcePath { get; set; }
    public string Id => Scene["board"]?["id"]?.GetValue<string>() ?? "local";
    public string Title => Scene["board"]?["title"]?.GetValue<string>() ?? "未命名白板";
    public DateTimeOffset? LastModifiedUtc => BoardMetadata.GetLastModified(this);
    public string Status => Scene["diagnostics"]?["status"]?.GetValue<string>() ?? "partial";
    public int ElementCount => (Scene["elements"] as JsonArray)?.Count ?? 0;
}

public interface IBoardNormalizer
{
    Task<JsonObject> NormalizeAsync(string html, string? commentsJson, string id, string title, CancellationToken cancellationToken = default);
}

public enum ExportFormat { Archive, Json, Pdf, Png, Jpeg, Svg, Html }
public enum PdfLayout { A4Fit = 0, A3Fit = 1, A4Tiles = 2, Canvas = 3 }
// 1 means 100%; raster dimensions are uniformly limited to 20000 × 20000.
public sealed record ExportOptions(ExportFormat Format, double ImageScale = 1, PdfLayout PdfLayout = PdfLayout.Canvas, bool Transparent = false);
public interface IExportService
{
    Task<IReadOnlyList<string>> ExportAsync(BoardDocument document, ExportOptions options, string targetPath, CancellationToken cancellationToken = default);
}
