using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhiteboardExporter.Core;

/// <summary>Stable task identity for resuming interrupted batches without stale completion reuse.</summary>
public static class ExportTaskState
{
    public static string Begin(JsonObject state, IEnumerable<string> selectedIds, string settingsKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(selectedIds);
        ArgumentNullException.ThrowIfNull(settingsKey);
        var ids = selectedIds.ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("白板 ID 不能为空。", nameof(selectedIds));
        ids = ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var selection = new JsonArray();
        foreach (var id in ids) selection.Add(id);
        var identity = new JsonObject { ["selectedIds"] = selection, ["settingsKey"] = settingsKey };
        var taskKey = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity)));
        var previous = state["$task"] as JsonObject;
        var previousRunId = GetString(previous, "runId");
        var runId = GetString(previous, "taskKey") == taskKey && GetString(previous, "status") == "running" &&
            GetString(previous, "settingsKey") == settingsKey && MatchesIds(previous?["selectedIds"], ids) &&
            Guid.TryParseExact(previousRunId, "N", out _)
            ? previousRunId : Guid.NewGuid().ToString("N");
        identity["taskKey"] = taskKey;
        identity["runId"] = runId;
        identity["status"] = "running";
        state["$task"] = identity;
        return runId!;
    }

    public static void Complete(JsonObject state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state["$task"] is JsonObject task) task["status"] = "done";
    }

    public static string CloudSignature(DateTimeOffset? modified, string runId)
    {
        if (modified is { } timestamp) return timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return runId;
    }

    public static string DocumentFingerprint(BoardDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFrame(hash, "WhiteboardExporter.DocumentFingerprint/v1"u8);
        AppendFrame(hash, JsonSerializer.SerializeToUtf8Bytes(document.Scene));
        AppendFiles(hash, "assets", document.Assets);
        AppendFiles(hash, "originals", document.Originals);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Scene/resource revision identity; raw originals need their own equality check before discarding a capture.</summary>
    public static string SceneFingerprint(BoardDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var scene = (JsonObject)document.Scene.DeepClone();
        if (scene["board"] is JsonObject board) board.Remove("capturedUtc");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFrame(hash, "WhiteboardExporter.SceneFingerprint/v1"u8);
        AppendFrame(hash, CanonicalJson(scene));
        AppendFiles(hash, "assets", document.Assets);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static byte[] CanonicalJson(JsonNode? node) => JsonSerializer.SerializeToUtf8Bytes(SortProperties(node));

    private static JsonNode? SortProperties(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var copy = new JsonObject();
            foreach (var pair in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)) copy[pair.Key] = SortProperties(pair.Value);
            return copy;
        }
        if (node is JsonArray array)
        {
            var copy = new JsonArray();
            foreach (var child in array) copy.Add(SortProperties(child));
            return copy;
        }
        return node?.DeepClone();
    }

    private static void AppendFiles(IncrementalHash hash, string section, Dictionary<string, byte[]> files)
    {
        AppendFrame(hash, Encoding.UTF8.GetBytes(section));
        Span<byte> count = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(count, files.Count);
        AppendFrame(hash, count);
        foreach (var pair in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            AppendFrame(hash, Encoding.UTF8.GetBytes(pair.Key));
            AppendFrame(hash, pair.Value);
        }
    }

    private static void AppendFrame(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static string? GetString(JsonObject? obj, string property) =>
        obj?[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool MatchesIds(JsonNode? previous, string[] ids)
    {
        if (previous is not JsonArray values || values.Count != ids.Length) return false;
        for (var index = 0; index < values.Count; index++)
            if (values[index] is not JsonValue value || !value.TryGetValue<string>(out var id) || id != ids[index]) return false;
        return true;
    }
}
