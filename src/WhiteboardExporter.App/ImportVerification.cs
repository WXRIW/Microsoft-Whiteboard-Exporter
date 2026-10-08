using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WhiteboardExporter.App.Services;
using WhiteboardExporter.Core;

namespace WhiteboardExporter.App;

/// <summary>Local-file regression; only known public brush resources may be fetched before offline rendering.</summary>
internal static class ImportVerification
{
    public static async Task<bool> RunAsync(MainWindow window, string manifestPath, string directory)
    {
        Directory.CreateDirectory(directory);
        var paths = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(manifestPath)) ?? [];
        if (paths.Length is 0 or > 100) throw new InvalidDataException("输入清单须为包含 1 至 100 个本地文件路径的 JSON 数组。");
        var results = new JsonArray();
        var success = true;
        foreach (var input in paths)
        {
            var path = Path.GetFullPath(input);
            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..16];
            var result = new JsonObject { ["inputKey"] = key, ["extension"] = Path.GetExtension(path) };
            results.Add(result);
            try
            {
                var document = await window.Importer.ImportAsync(path);
                result["recoveredAssets"] = await window.RecoverResourcesAsync(document);
                await window.ShowDocumentAsync(document);
                await window.ExportHost.LoadAsync(document);
                var metrics = await window.ExportHost.EvaluateAsync("window.wb.getExportMetrics()");
                result["contentStatus"] = document.Status;
                result["elementCount"] = document.ElementCount;
                result["diagnostics"] = new JsonArray((document.Scene["diagnostics"]?["items"] as JsonArray ?? []).Select(item => (JsonNode?)JsonValue.Create(item?["code"]?.GetValue<string>())).ToArray());
                result["imageMetrics"] = metrics?.DeepClone();
                var formats = new JsonObject(); result["formats"] = formats;
                var folder = Path.Combine(directory, key);
                foreach (var format in Enum.GetValues<ExportFormat>())
                {
                    var extension = format switch { ExportFormat.Archive => "wbarchive", ExportFormat.Json => "json", ExportFormat.Pdf => "pdf", ExportFormat.Png => "png", ExportFormat.Jpeg => "jpg", ExportFormat.Svg => "svg", _ => "html" };
                    var outputPath = Path.Combine(folder, "board." + extension);
                    try
                    {
                        var outputs = await window.Exporter.ExportAsync(document, new ExportOptions(format), outputPath);
                        formats[format.ToString()] = new JsonObject { ["status"] = "complete", ["files"] = outputs.Count };
                        if (format == ExportFormat.Pdf) await window.ShowPdfAsync(outputPath);
                        if (format == ExportFormat.Png) window.ShowImage(outputPath);
                    }
                    catch (Exception ex) { success = false; formats[format.ToString()] = new JsonObject { ["status"] = "failed", ["error"] = ex.Message }; }
                }
                result["success"] = formats.All(entry => entry.Value?["status"]?.GetValue<string>() == "complete");
            }
            catch (Exception ex) { success = false; result["success"] = false; result["error"] = ex.Message; }
            await File.WriteAllTextAsync(Path.Combine(directory, "import-result.json"), new JsonObject { ["success"] = success, ["inputs"] = results.DeepClone() }.ToJsonString(LocalWebHost.JsonOptions));
        }
        return success;
    }
}
