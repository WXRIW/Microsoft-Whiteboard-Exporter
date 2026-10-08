using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WhiteboardExporter.Core;

/// <summary>Source edit time is distinct from capture time and file modification time.</summary>
public static partial class BoardMetadata
{
    private static readonly string[] IsoFields = ["lastModifiedTime", "lastModifiedDateTime", "modifiedTime", "modifiedDateTime", "lastEditedTime", "lastEditedDateTime"];

    public static DateTimeOffset? GetLastModified(BoardDocument document) => ReadTimestamp(document.Scene["board"]?["lastModifiedUtc"]);

    public static void SetLastModified(BoardDocument document, DateTimeOffset? modified, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        // Missing API information does not erase an authoritative date retained in a backup.
        if (modified is null) return;
        var board = document.Scene["board"] as JsonObject ?? throw new InvalidDataException("scene.board 缺失。");
        board["lastModifiedUtc"] = modified.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(source))
        {
            if (source.Length > 512) throw new ArgumentException("编辑时间来源说明过长。", nameof(source));
            board["lastModifiedSource"] = source;
        }
        else board.Remove("lastModifiedSource");
    }

    public static void SetCaptured(BoardDocument document, DateTimeOffset captured)
    {
        ArgumentNullException.ThrowIfNull(document);
        var board = document.Scene["board"] as JsonObject ?? throw new InvalidDataException("scene.board 缺失。");
        board["capturedUtc"] = captured.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    public static void ApplyCloudMetadata(BoardDocument document, DateTimeOffset? modified, string? source = null, DateTimeOffset? capturedAt = null)
    {
        SetLastModified(document, modified, source);
        if (capturedAt is { } captured) SetCaptured(document, captured);
    }

    public static (DateTimeOffset? Modified, string? Source) ReadMicrosoftLastModified(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return (null, null);
        foreach (var field in IsoFields)
            if (item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String && TryParseTimestamp(value.GetString(), out var timestamp))
                return (timestamp, "microsoft-api:" + field);
        foreach (var milliseconds in new[] { true, false })
        {
            var suffix = milliseconds ? "UnixMilliseconds" : "UnixSeconds";
            foreach (var prefix in new[] { "lastModifiedTime", "lastModified", "lastModifiedDateTime", "modifiedTime", "modified", "lastEditedTime", "lastEdited" })
            {
                var field = prefix + suffix;
                if (!item.TryGetProperty(field, out var value) || !TryReadInteger(value, out var epoch)) continue;
                try
                {
                    var timestamp = milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch);
                    return (timestamp, "microsoft-api:" + field);
                }
                catch (ArgumentOutOfRangeException) { }
            }
        }
        return (null, null);
    }

    public static bool TryParseTimestamp(string? text, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (text is null || text.Length > 64) return false;
        text = text.Trim();
        // A relative caption or a local clock must never acquire this computer's time zone.
        if (!TimestampPattern().IsMatch(text) || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        timestamp = parsed.ToUniversalTime();
        return true;
    }

    internal static DateTimeOffset? ReadTimestamp(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && TryParseTimestamp(text, out var parsed) ? parsed : null;

    internal static void PreserveBoardFields(BoardDocument source, BoardDocument target)
    {
        if (source.Scene["board"] is not JsonObject original || target.Scene["board"] is not JsonObject destination) return;
        foreach (var pair in original)
            if (pair.Key is not ("id" or "title")) destination[pair.Key] = pair.Value?.DeepClone();
    }

    private static bool TryReadInteger(JsonElement value, out long number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number ? value.TryGetInt64(out number) :
            value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number);
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:[zZ]|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();
}
