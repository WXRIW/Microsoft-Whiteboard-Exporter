using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhiteboardExporter.Core;

public sealed record ExportLibraryEntry
{
    public required string Key { get; init; }
    public required string DirectoryPath { get; init; }
    public string? BoardIdHint { get; init; }
    public required string TitleFallback { get; init; }
    public string? AccountDirectory { get; init; }
    public string? AccountLabel { get; init; }
    public ExportAccount? Account { get; init; }
    public required IReadOnlyList<string> SourceCandidates { get; init; }
    public string? PdfPath { get; init; }
    public required IReadOnlyList<string> ImagePaths { get; init; }
    public string? HtmlPath { get; init; }
    public string? SvgPath { get; init; }
    public required IReadOnlyDictionary<string, string> MediaPaths { get; init; }
}

public sealed record ExportLibraryIssue(string Path, string Code, string Message);
public sealed record ExportLibraryScanResult(string RootDirectory, IReadOnlyList<ExportLibraryEntry> Entries, IReadOnlyList<ExportLibraryIssue> Issues);
public sealed record ExportLibraryScanOptions(int MaxDepth = 12, int MaxFileSystemEntries = 100_000, int MaxBoards = 20_000);

/// <summary>Read-only local export discovery. Importing, rendering and remote recovery remain separate actions.</summary>
public static partial class ExportLibraryService
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    { "source", "assets", "originals", "responses", "profiles", "browser", "resources", ".history", ".git", ".vs", "bin", "obj" };
    private static readonly HashSet<string> ControlJson = new(StringComparer.OrdinalIgnoreCase)
    { "export-state.json", "manifest.json", "account.json", "account-metadata.json", "cloud-result.json", "cloud-metadata-result.json", "smoke-result.json", "import-result.json", "output-qa.json", "修复结果.json" };
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".wbarchive", ".json", ".zip", ".html", ".htm", ".pdf", ".png", ".jpg", ".jpeg", ".svg" };

    public static async Task<ExportLibraryScanResult> ScanAsync(string directory, CancellationToken cancellationToken = default, ExportLibraryScanOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        options ??= new();
        if (options.MaxDepth is < 0 or > 64 || options.MaxFileSystemEntries is < 1 or > 1_000_000 || options.MaxBoards is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(options), "扫描限制超出允许范围。");
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var scanned = await Task.Run(() => Scan(root, options, cancellationToken), cancellationToken);
        var entries = new List<ExportLibraryEntry>(scanned.Entries.Count);
        var issues = scanned.Issues.ToList();
        var accounts = new Dictionary<string, ExportAccount?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in scanned.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = entry;
            for (var current = entry.DirectoryPath; IsUnder(root, current); current = Path.GetDirectoryName(current) ?? "")
            {
                if (string.IsNullOrEmpty(current)) break;
                if (!accounts.TryGetValue(current, out var account))
                {
                    account = null;
                    var manifest = Path.Combine(current, "account.json");
                    if (File.Exists(manifest))
                    {
                        try
                        {
                            if (HasReparsePoint(manifest, root)) throw new InvalidDataException("账号清单路径经过符号链接或目录联接。");
                            account = await AccountDirectoryService.ReadAccountAsync(current, cancellationToken);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException)
                        { AddIssue(issues, manifest, "AccountMetadataUnreadable", "无法读取账号清单，按本地目录继续分组。"); }
                    }
                    accounts[current] = account;
                }
                if (account is not null)
                {
                    resolved = entry with { Account = account, AccountDirectory = current, AccountLabel = account.DisplayName };
                    break;
                }
                if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
            }
            entries.Add(resolved);
        }
        return new(root, entries, issues);
    }

    private static ExportLibraryScanResult Scan(string root, ExportLibraryScanOptions options, CancellationToken cancellationToken)
    {
        var entries = new List<ExportLibraryEntry>();
        var issues = new List<ExportLibraryIssue>();
        if (!Directory.Exists(root))
        { AddIssue(issues, root, "DirectoryMissing", "所选目录不存在。"); return new(root, entries, issues); }
        if (HasReparsePoint(root, Path.GetPathRoot(root)!))
        { AddIssue(issues, root, "ReparsePointSkipped", "不扫描符号链接或目录联接。"); return new(root, entries, issues); }
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0));
        var visited = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, depth) = pending.Pop();
            var files = new List<FileCandidate>();
            try
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++visited > options.MaxFileSystemEntries)
                    { AddIssue(issues, root, "ScanEntryLimit", "目录项数量超过扫描限制，已显示已找到的白板。"); pending.Clear(); break; }
                    try
                    {
                    var absolute = Path.GetFullPath(child);
                    if (!IsUnder(root, absolute))
                    { AddIssue(issues, child, "OutsideRootSkipped", "跳过超出所选目录的路径。"); continue; }
                    var attributes = File.GetAttributes(absolute);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    { AddIssue(issues, absolute, "ReparsePointSkipped", "不扫描符号链接或目录联接。"); continue; }
                    var name = Path.GetFileName(absolute);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (ExcludedDirectories.Contains(name) || HistoricalName().IsMatch(name)) continue;
                        if (depth >= options.MaxDepth)
                        { AddIssue(issues, absolute, "ScanDepthLimit", "子目录超过扫描深度限制。"); continue; }
                        pending.Push((absolute, depth + 1));
                        continue;
                    }
                    var extension = Path.GetExtension(name).ToLowerInvariant();
                    var stem = Path.GetFileNameWithoutExtension(name);
                    if (!SupportedExtensions.Contains(extension) || HistoricalName().IsMatch(stem) || name.StartsWith('.') ||
                        (extension == ".json" && ControlJson.Contains(name))) continue;
                    var header = extension == ".json" ? ReadSceneHeader(absolute, cancellationToken) : null;
                    var suffix = SuffixIdentity(stem);
                    if (extension == ".json" && header is null && suffix is null && !name.Equals("scene.json", StringComparison.OrdinalIgnoreCase)) continue;
                    files.Add(new(absolute, extension, stem, suffix ?? header?.Id, header?.Title, File.GetLastWriteTimeUtc(absolute)));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
                    { AddIssue(issues, child, "FileUnreadable", "无法读取该目录项，继续扫描其他文件。"); }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            { AddIssue(issues, path, "DirectoryUnreadable", "无法完整读取该目录，已保留其他可用目录。"); }
            foreach (var entry in GroupFiles(root, path, files))
            {
                if (entries.Count >= options.MaxBoards)
                { AddIssue(issues, root, "ScanBoardLimit", "白板数量超过扫描限制，已显示已找到的白板。"); return new(root, entries, issues); }
                entries.Add(entry);
            }
            if (visited > options.MaxFileSystemEntries) break;
        }
        return new(root, entries.OrderBy(entry => entry.AccountLabel, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.TitleFallback, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Key, StringComparer.Ordinal).ToArray(), issues);
    }

    private static IEnumerable<ExportLibraryEntry> GroupFiles(string root, string directory, List<FileCandidate> files)
    {
        if (files.Count == 0) yield break;
        var folderIdentity = SuffixIdentity(Path.GetFileName(directory));
        var stemIdentities = files.Where(file => file.Id is not null).GroupBy(file => file.Stem, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(file => file.Id!).Distinct(StringComparer.OrdinalIgnoreCase).First(), StringComparer.OrdinalIgnoreCase);
        var groups = files.GroupBy(file => file.Id ?? (stemIdentities.TryGetValue(file.Stem, out var known) ? known : folderIdentity) ?? "stem:" + file.Stem, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var ordered = group.OrderBy(file => file.LastWriteUtc).ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            var preferred = ordered.OrderBy(file => SourceRank(file.Extension)).ThenByDescending(file => file.LastWriteUtc).ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            var sources = preferred.Where(file => SourceRank(file.Extension) < 10).Select(file => file.Path).ToArray();
            var first = preferred[0];
            var id = group.Key.StartsWith("stem:", StringComparison.Ordinal) ? null : group.Key;
            var stem = first.Stem == "scene" && folderIdentity is not null ? Path.GetFileName(directory) : first.Stem;
            var title = preferred.Select(file => file.Title).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? StripIdentity(stem, id);
            var media = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var extension in new[] { ".pdf", ".png", ".jpg", ".jpeg", ".html", ".htm", ".svg" })
                if (ordered.LastOrDefault(file => file.Extension == extension) is { } found) media[extension] = found.Path;
            var accountDirectory = InferAccountDirectory(root, directory, folderIdentity);
            yield return new()
            {
                Key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant() + "\0" + group.Key.ToUpperInvariant()))),
                DirectoryPath = directory, BoardIdHint = id, TitleFallback = string.IsNullOrWhiteSpace(title) ? "未命名白板" : title,
                AccountDirectory = accountDirectory, AccountLabel = accountDirectory is null ? null : Path.GetFileName(accountDirectory),
                SourceCandidates = sources, PdfPath = media.GetValueOrDefault(".pdf"), ImagePaths = new[] { ".png", ".jpg", ".jpeg" }.Where(media.ContainsKey).Select(extension => media[extension]).ToArray(),
                HtmlPath = media.GetValueOrDefault(".html") ?? media.GetValueOrDefault(".htm"), SvgPath = media.GetValueOrDefault(".svg"),
                MediaPaths = media
            };
        }
    }

    private static int SourceRank(string extension) => extension switch { ".wbarchive" => 0, ".json" => 1, ".zip" => 2, ".html" or ".htm" => 3, _ => 10 };
    private static string? InferAccountDirectory(string root, string directory, string? folderIdentity)
    {
        if (directory.Equals(root, StringComparison.OrdinalIgnoreCase)) return null;
        var candidate = folderIdentity is not null ? Path.GetDirectoryName(directory) : directory;
        return candidate is not null && !candidate.Equals(root, StringComparison.OrdinalIgnoreCase) && IsUnder(root, candidate) ? candidate : null;
    }

    private static string? SuffixIdentity(string stem)
    {
        var index = stem.LastIndexOf("__", StringComparison.Ordinal);
        if (index < 0 || index + 2 >= stem.Length) return null;
        var suffix = stem[(index + 2)..];
        return suffix.Length <= 512 && suffix.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.') ? suffix : null;
    }
    private static string StripIdentity(string stem, string? identity) => identity is not null && stem.EndsWith("__" + identity, StringComparison.OrdinalIgnoreCase) ? stem[..^(identity.Length + 2)] : stem;
    private static bool IsUnder(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool HasReparsePoint(string path, string root)
    {
        for (var current = path; IsUnder(root, current); current = Path.GetDirectoryName(current) ?? "")
        {
            if (string.IsNullOrEmpty(current)) break;
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return false;
    }

    private static SceneHeader? ReadSceneHeader(string path, CancellationToken cancellationToken)
    {
        const int limit = 256 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[Math.Min(limit, checked((int)Math.Min(stream.Length, limit)))];
        var count = 0;
        while (count < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, count, bytes.Length - count); if (read == 0) break; count += read;
        }
        var start = count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        var reader = new Utf8JsonReader(bytes.AsSpan(start, count - start), isFinalBlock: count == stream.Length, state: default);
        string? id = null, title = null;
        var versionOne = false;
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
                if (reader.ValueTextEquals("schemaVersion"))
                { if (reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var version)) versionOne = version == 1; }
                else if (reader.ValueTextEquals("board") && reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                {
                    while (reader.Read() && !(reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 1))
                    {
                        if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 2) continue;
                        if (reader.ValueTextEquals("id")) { if (reader.Read() && reader.TokenType == JsonTokenType.String) id = reader.GetString(); }
                        else if (reader.ValueTextEquals("title")) { if (reader.Read() && reader.TokenType == JsonTokenType.String) title = reader.GetString(); }
                    }
                }
                if (versionOne && !string.IsNullOrWhiteSpace(id) && id.Length <= 512 && (title is null || title.Length <= 4096)) return new(id, title);
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static void AddIssue(List<ExportLibraryIssue> issues, string path, string code, string message)
    { if (issues.Count < 1000) issues.Add(new(path, code, message)); }

    [GeneratedRegex(@"(?:^|\.)previous-[a-f0-9]{12,32}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HistoricalName();
    private sealed record FileCandidate(string Path, string Extension, string Stem, string? Id, string? Title, DateTime LastWriteUtc);
    private sealed record SceneHeader(string Id, string? Title);
}
