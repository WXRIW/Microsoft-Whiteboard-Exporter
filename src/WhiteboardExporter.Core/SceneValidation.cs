using System.Text.Json.Nodes;

namespace WhiteboardExporter.Core;

/// <summary>Validates the public SceneDocument v1 structure before local rendering or persistence.</summary>
public static class SceneValidation
{
    private const int MaxElements = 100_000;
    private const double MaxCoordinate = 100_000_000;

    public static void Validate(JsonObject scene)
    {
        if (scene["schemaVersion"] is not JsonValue version || !version.TryGetValue<int>(out var versionNumber) || versionNumber != 1)
            throw new InvalidDataException("仅支持 SceneDocument schemaVersion 1。");
        if (scene["board"] is not JsonObject board) throw new InvalidDataException("scene.board 缺失。");
        ReadString(board, "id", true, 512);
        ReadString(board, "title", true, 4096);
        ReadString(board, "lastModifiedSource", false, 512);
        foreach (var field in new[] { "lastModifiedUtc", "capturedUtc" })
        {
            var timestamp = ReadString(board, field, false, 64);
            if (timestamp is not null && !BoardMetadata.TryParseTimestamp(timestamp, out _))
                throw new InvalidDataException($"board.{field} 必须为带时区的 ISO 8601 时间。");
        }
        ReadString(scene, "background", false, 256);
        ValidateBounds(scene["bounds"], "scene.bounds", required: true);
        if (scene["elements"] is not JsonArray elements) throw new InvalidDataException("scene.elements 必须为数组。");
        if (scene["assets"] is not JsonObject assets) throw new InvalidDataException("scene.assets 必须为对象。");
        if (assets.Count > 20_000) throw new InvalidDataException("资源数量超过限制。");
        foreach (var pair in assets)
        {
            if (pair.Key.Length == 0 || pair.Key.Length > 512 || pair.Value is not JsonObject asset)
                throw new InvalidDataException("资源 ID 或资源元数据无效。");
            var path = ReadString(asset, "path", false, 1024);
            if (!string.IsNullOrEmpty(path)) StorageSafety.SafeRelativePath(path);
            ReadString(asset, "mime", false, 128);
            ReadString(asset, "source", false, 8192);
            ReadString(asset, "dataUrl", false, checked((int)StorageSafety.MaxFileBytes * 2));
        }
        if (scene["diagnostics"] is not JsonObject diagnostics)
            throw new InvalidDataException("scene.diagnostics 缺失。");
        var status = ReadString(diagnostics, "status", true, 20);
        if (status is not ("complete" or "partial" or "failed")) throw new InvalidDataException("诊断状态必须为 complete、partial 或 failed。");
        foreach (var key in new[] { "total", "supported", "unsupported" })
        {
            if (diagnostics[key] is not JsonValue value || !value.TryGetValue<int>(out var count) || count < 0 || count > MaxElements)
                throw new InvalidDataException($"diagnostics.{key} 必须为有效的非负整数。");
        }
        if (diagnostics["missingAssets"] is not JsonArray missingAssets || diagnostics["items"] is not JsonArray diagnosticItems)
            throw new InvalidDataException("diagnostics.missingAssets 和 items 必须为数组。");
        foreach (var item in missingAssets)
            if (item is not JsonValue assetId || !assetId.TryGetValue<string>(out var text) || text.Length > 512)
                throw new InvalidDataException("diagnostics.missingAssets 必须包含资源 ID 字符串。");
        foreach (var item in diagnosticItems)
        {
            if (item is not JsonObject diagnostic) throw new InvalidDataException("诊断条目必须为对象。");
            foreach (var key in new[] { "code", "elementId", "assetId", "severity", "message" }) ReadString(diagnostic, key, false, 16 * 1024);
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var totalElements = 0;
        ValidateElements(elements, 0, ids, ref totalElements);
    }

    private static void ValidateElements(JsonArray elements, int depth, HashSet<string> ids, ref int total)
    {
        if (depth > 32) throw new InvalidDataException("分组嵌套深度超过限制。");
        foreach (var node in elements)
        {
            if (++total > MaxElements) throw new InvalidDataException("白板元素数量超过限制。");
            if (node is not JsonObject element) throw new InvalidDataException("白板元素必须为 JSON 对象。");
            var id = ReadString(element, "id", true, 512)!;
            if (!ids.Add(id)) throw new InvalidDataException($"白板元素 ID 重复：{id}");
            ReadString(element, "kind", true, 128);
            ReadString(element, "nativeType", false, 128);
            ReadString(element, "nativeId", false, 512);
            if (element["z"] is not null) FiniteNumber(element["z"], "element.z");
            if (element["transform"] is not null)
            {
                if (element["transform"] is not JsonArray transform || transform.Count != 6)
                    throw new InvalidDataException("元素变换必须为六参数数组 [a,b,c,d,e,f]。");
                foreach (var coefficient in transform) FiniteNumber(coefficient, "element.transform");
            }
            if (element["opacity"] is not null)
            {
                var opacity = FiniteNumber(element["opacity"], "element.opacity");
                if (opacity is < 0 or > 1) throw new InvalidDataException("透明度必须在 0 至 1 范围内。");
            }
            if (element["style"] is not null)
            {
                if (element["style"] is not JsonObject style) throw new InvalidDataException("元素 style 必须为对象。");
                if (style["opacity"] is not null)
                {
                    var opacity = FiniteNumber(style["opacity"], "element.style.opacity");
                    if (opacity is < 0 or > 1) throw new InvalidDataException("透明度必须在 0 至 1 范围内。");
                }
            }
            ValidateBounds(element["bounds"], "element.bounds", false);
            foreach (var key in new[] { "svg", "html", "text", "raw", "sourceFragment" }) ReadString(element, key, false, 32 * 1024 * 1024);
            ReadString(element, "assetId", false, 512);
            if (element["children"] is not null)
            {
                if (element["children"] is not JsonArray children) throw new InvalidDataException("元素 children 必须为数组。");
                ValidateElements(children, depth + 1, ids, ref total);
            }
        }
    }

    private static void ValidateBounds(JsonNode? node, string label, bool required)
    {
        if (node is null && !required) return;
        if (node is not JsonObject bounds) throw new InvalidDataException($"{label} 必须为对象。");
        foreach (var key in new[] { "x", "y", "width", "height" })
        {
            var value = FiniteNumber(bounds[key], $"{label}.{key}");
            if ((key is "width" or "height") && value < 0) throw new InvalidDataException($"{label}.{key} 不能为负数。");
        }
    }

    private static double FiniteNumber(JsonNode? node, string label)
    {
        if (node is not JsonValue value) throw new InvalidDataException($"{label} 必须为数值。");
        double number;
        if (value.TryGetValue<double>(out var d)) number = d;
        else if (value.TryGetValue<int>(out var i)) number = i;
        else if (value.TryGetValue<long>(out var l)) number = l;
        else if (value.TryGetValue<float>(out var f)) number = f;
        else if (value.TryGetValue<decimal>(out var m)) number = (double)m;
        else throw new InvalidDataException($"{label} 必须为数值。");
        if (!double.IsFinite(number) || Math.Abs(number) > MaxCoordinate)
            throw new InvalidDataException($"{label} 必须为有限数值，绝对值不能超过 {MaxCoordinate}。");
        return number;
    }

    private static string? ReadString(JsonObject obj, string key, bool required, int maxLength)
    {
        var node = obj[key];
        if (node is null && !required) return null;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text) || text.Length > maxLength || (required && string.IsNullOrWhiteSpace(text)))
            throw new InvalidDataException($"{key} 必须为有效字符串。");
        return text;
    }
}
