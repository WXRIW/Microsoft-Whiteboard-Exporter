using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhiteboardExporter.Core;

public static class AccountDirectoryService
{
    public const string ManifestName = "account.json";
    private const int ManifestLimit = 16384;

    public static async Task<ExportAccount?> ReadAccountAsync(string directory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(Path.GetFullPath(directory), ManifestName);
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > ManifestLimit)
            throw new InvalidDataException("账号目录清单过大或为链接。");
        JsonNode? json;
        try { json = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)); }
        catch (JsonException exception) { throw new InvalidDataException("账号目录清单损坏。", exception); }
        if (json is not JsonObject || json["schemaVersion"] is not JsonValue version || !version.TryGetValue<int>(out var value) || value != 1)
            throw new InvalidDataException("不支持的账号目录清单版本。");
        return ExportAccount.FromJson(json["account"]) ?? throw new InvalidDataException("账号目录清单缺少账号信息。");
    }

    public static async Task<string> ResolveAsync(string exportRoot, ExportAccount account, CancellationToken cancellationToken = default)
    {
        account = account.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(exportRoot);
        RejectLinkedAncestors(root);
        Directory.CreateDirectory(root);
        var suffix = "__" + account.Key[..16];
        var matches = new List<string>();
        foreach (var child in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
            if (!Path.GetFileName(child).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if ((await ReadAccountAsync(child, cancellationToken))?.Key == account.Key) matches.Add(child);
            }
            catch (InvalidDataException) { }
        }
        if (matches.Count > 1) throw new InvalidDataException("同一账号有多个目录，无法确定应使用哪一个。请保留一个当前账号目录后重试。");
        var folder = matches.SingleOrDefault() ?? Path.Combine(root, SafeLabel(account.Email ?? account.DisplayName) + suffix);
        if (!string.Equals(Path.GetDirectoryName(folder), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("账号目录超出输出根目录。");
        RejectLinkedAncestors(folder);
        var prior = Directory.Exists(folder) ? await ReadAccountAsync(folder, cancellationToken) : null;
        if (prior is not null && prior.Key != account.Key) throw new InvalidDataException("账号目录已属于另一个账号。");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, ManifestName);
        var contents = new JsonObject { ["schemaVersion"] = 1, ["account"] = account.ToJson() }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (File.Exists(path) && await File.ReadAllTextAsync(path, cancellationToken) == contents) return folder;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, contents, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return folder;
    }

    public static string SafeLabel(string value)
    {
        var characters = value.Trim().Select(c => c < 32 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' ? '_' : c).ToArray();
        var label = new string(characters).Trim(' ', '.');
        if (label.Length == 0) label = "账号";
        return label.Length <= 60 ? label : label[..60].TrimEnd(' ', '.');
    }

    private static void RejectLinkedAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("输出目录不能经过目录链接。");
    }
}
