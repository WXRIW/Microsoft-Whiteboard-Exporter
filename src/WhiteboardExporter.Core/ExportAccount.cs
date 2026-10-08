using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WhiteboardExporter.Core;

/// <summary>Public account identity used only for local export namespaces.</summary>
public sealed partial record ExportAccount(string Key, string DisplayName, string? Email = null, string Kind = "local", string? Source = null)
{
    public static ExportAccount Local { get; } = FromLabel("本地导入", "local");

    public static ExportAccount FromLabel(string label, string kind = "manual")
    {
        label = label.Trim().Normalize(NormalizationForm.FormC);
        if (label.Length is 0 or > 200) throw new InvalidDataException("账号目录名称需为 1 至 200 个字符。");
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("WhiteboardExporter.Account/v1/" + kind + "/" + label.ToUpperInvariant())));
        return new ExportAccount(key, label, Kind: kind, Source: "local-account-label").Validate();
    }

    public ExportAccount Validate()
    {
        if (string.IsNullOrEmpty(Key) || !KeyPattern().IsMatch(Key) || string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 200 ||
            Email?.Length > 320 || Kind is not ("local" or "manual" or "msa" or "aad") || Source?.Length > 512)
            throw new InvalidDataException("账号信息的标识或名称无效。");
        return this with { Key = Key.ToLowerInvariant(), DisplayName = DisplayName.Trim() };
    }

    public JsonObject ToJson()
    {
        var value = Validate();
        var json = new JsonObject { ["key"] = value.Key, ["displayName"] = value.DisplayName, ["kind"] = value.Kind };
        if (!string.IsNullOrWhiteSpace(value.Email)) json["email"] = value.Email;
        if (!string.IsNullOrWhiteSpace(value.Source)) json["source"] = value.Source;
        return json;
    }

    public static ExportAccount? FromJson(JsonNode? node)
    {
        if (node is null) return null;
        if (node is not JsonObject json) throw new InvalidDataException("账号信息格式无效。");
        string? Read(string key) => json[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        return new ExportAccount(Read("key") ?? "", Read("displayName") ?? "", Read("email"), Read("kind") ?? "local", Read("source")).Validate();
    }

    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
