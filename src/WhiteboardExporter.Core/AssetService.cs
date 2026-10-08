using System.Text;
using System.Text.Json.Nodes;

namespace WhiteboardExporter.Core;

/// <summary>Moves image bytes out of scene JSON and supplies temporary data URLs for offline rendering.</summary>
public static class AssetService
{
    public static void ExtractEmbeddedAssets(BoardDocument document)
    {
        if (document.Scene["assets"] is not JsonObject assets) throw new InvalidDataException("scene.assets 必须为对象。");
        foreach (var pair in assets.ToArray())
        {
            if (pair.Value is not JsonObject asset) continue;
            var dataUrl = asset["dataUrl"]?.GetValue<string>();
            var source = asset["source"]?.GetValue<string>();
            if (dataUrl is null && source?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true) dataUrl = source;
            if (dataUrl is not null)
            {
                var (bytes, mime) = DecodeDataUrl(dataUrl);
                AddAsset(document, pair.Key, bytes, mime);
                asset.Remove("dataUrl");
                if (source?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true) asset.Remove("source");
            }
            else if (asset["path"]?.GetValue<string>() is { } path && document.Assets.TryGetValue(path, out var bytes))
            {
                AddAsset(document, pair.Key, bytes, asset["mime"]?.GetValue<string>() ?? MimeForPath(path));
            }
        }
        RefreshDiagnostics(document);
    }

    public static void AddAsset(BoardDocument document, string assetId, byte[] bytes, string? mime = null)
    {
        if (bytes.LongLength > StorageSafety.MaxFileBytes) throw new InvalidDataException("图片资源超过大小限制。");
        if (document.Scene["assets"] is not JsonObject assets || assets[assetId] is not JsonObject asset)
            throw new InvalidDataException($"不存在资源元数据：{assetId}");
        mime = NormalizeMime(mime);
        var path = $"assets/{StorageSafety.Hash(bytes)}{ExtensionForMime(mime)}";
        document.Assets[path] = bytes;
        asset["path"] = path;
        asset["mime"] = mime;
        asset.Remove("dataUrl");
    }

    public static JsonObject HydrateScene(BoardDocument document)
    {
        SceneValidation.Validate(document.Scene);
        var scene = (JsonObject)document.Scene.DeepClone();
        foreach (var pair in (JsonObject)scene["assets"]!)
        {
            if (pair.Value is not JsonObject asset || asset["path"]?.GetValue<string>() is not { } path) continue;
            if (asset["invalid"] is JsonValue flag && flag.TryGetValue<bool>(out var invalid) && invalid) continue;
            if (document.Assets.TryGetValue(path, out var bytes))
                asset["dataUrl"] = $"data:{NormalizeMime(asset["mime"]?.GetValue<string>())};base64,{Convert.ToBase64String(bytes)}";
        }
        return scene;
    }

    public static void RefreshDiagnostics(BoardDocument document)
    {
        if (document.Scene["assets"] is not JsonObject assets || document.Scene["diagnostics"] is not JsonObject diagnostics)
            throw new InvalidDataException("资源或诊断元数据缺失。");
        var missing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in assets)
        {
            if (pair.Value is not JsonObject asset) continue;
            var path = asset["path"]?.GetValue<string>();
            if (path is null || !document.Assets.ContainsKey(path)) missing.Add(pair.Key);
        }
        foreach (var element in EnumerateElements(document.Scene["elements"] as JsonArray))
            if (element["assetId"]?.GetValue<string>() is { } id && !assets.ContainsKey(id)) missing.Add(id);
        var missingArray = new JsonArray();
        foreach (var id in missing.Order(StringComparer.Ordinal)) missingArray.Add(id);
        diagnostics["missingAssets"] = missingArray;
        if (diagnostics["items"] is not JsonArray items) diagnostics["items"] = items = new JsonArray();
        for (var index = items.Count - 1; index >= 0; index--)
            if (items[index] is JsonObject item && (item["code"]?.GetValue<string>() == "missing-asset" ||
                (missing.Count == 0 && item["code"]?.GetValue<string>() == "ASSET_UNAVAILABLE"))) items.RemoveAt(index);
        foreach (var id in missing.Order(StringComparer.Ordinal))
            items.Add(new JsonObject { ["code"] = "missing-asset", ["assetId"] = id, ["message"] = "资源未包含在本地数据中；离线预览将显示占位。" });
        var unsupported = diagnostics["unsupported"]?.GetValue<int>() ?? 0;
        var warnings = items.OfType<JsonObject>().Any(item => item["severity"]?.GetValue<string>() is "warning" or "error");
        if (diagnostics["status"]?.GetValue<string>() != "failed") diagnostics["status"] = missing.Count == 0 && unsupported == 0 && !warnings ? "complete" : "partial";
    }

    internal static IEnumerable<JsonObject> EnumerateElements(JsonArray? elements)
    {
        if (elements is null) yield break;
        foreach (var node in elements)
        {
            if (node is not JsonObject element) continue;
            yield return element;
            foreach (var child in EnumerateElements(element["children"] as JsonArray)) yield return child;
        }
    }

    internal static (byte[] Bytes, string Mime) DecodeDataUrl(string value)
    {
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || value.Length > StorageSafety.MaxFileBytes * 2)
            throw new InvalidDataException("无效或过大的 data URL。");
        var comma = value.IndexOf(',');
        if (comma < 5) throw new InvalidDataException("data URL 缺少内容。");
        var headers = value[5..comma].Split(';');
        var mime = NormalizeMime(headers[0]);
        try
        {
            var bytes = headers.Any(h => h.Equals("base64", StringComparison.OrdinalIgnoreCase))
                ? Convert.FromBase64String(value[(comma + 1)..])
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(value[(comma + 1)..]));
            if (bytes.LongLength > StorageSafety.MaxFileBytes) throw new InvalidDataException("资源超过大小限制。");
            return (bytes, mime);
        }
        catch (FormatException exception) { throw new InvalidDataException("data URL 编码无效。", exception); }
    }

    internal static string MimeForPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif",
        ".webp" => "image/webp", ".svg" => "image/svg+xml", ".bmp" => "image/bmp",
        _ => "application/octet-stream"
    };

    internal static string NormalizeMime(string? mime) => mime?.ToLowerInvariant() switch
    {
        "image/png" => "image/png", "image/jpeg" or "image/jpg" => "image/jpeg", "image/gif" => "image/gif",
        "image/webp" => "image/webp", "image/svg+xml" => "image/svg+xml", "image/bmp" => "image/bmp",
        _ => "application/octet-stream"
    };

    private static string ExtensionForMime(string mime) => mime switch
    {
        "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp",
        "image/svg+xml" => ".svg", "image/bmp" => ".bmp", _ => ".bin"
    };
}
