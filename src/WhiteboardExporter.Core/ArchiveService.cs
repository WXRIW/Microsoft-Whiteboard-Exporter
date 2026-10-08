using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhiteboardExporter.Core;

public static class ArchiveService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Ignores ZIP container times and manifest creation time, while verifying every retained data byte.</summary>
    public static async Task<bool> MatchesDocumentAsync(BoardDocument document, string archivePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(archivePath)) return false;
        var expected = CollectFiles(Prepare(document), includeOriginals: true);
        try
        {
            var bytes = await StorageSafety.ReadFileAsync(archivePath, cancellationToken, StorageSafety.MaxArchiveBytes);
            var existing = await StorageSafety.ReadZipAsync(bytes, cancellationToken);
            if (!existing.Remove("manifest.json", out var manifestBytes) || existing.Count != expected.Count) return false;
            var manifest = StorageSafety.ParseObject(manifestBytes, "manifest.json");
            if (manifest["format"]?.GetValue<string>() != "whiteboard-archive" || manifest["version"]?.GetValue<int>() != 1 ||
                manifest["sceneFile"]?.GetValue<string>() != "scene.json" || manifest["files"] is not JsonObject hashes || hashes.Count != existing.Count) return false;
            foreach (var file in existing)
                if (!expected.TryGetValue(file.Key, out var desired) || hashes[file.Key]?.GetValue<string>() != StorageSafety.Hash(file.Value) ||
                    (file.Key == "scene.json" ? !SameScene(file.Value, desired) : !file.Value.AsSpan().SequenceEqual(desired))) return false;
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        { return false; }
    }

    public static async Task<bool> SceneJsonMatchesAsync(BoardDocument document, string sceneJsonPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        sceneJsonPath = Path.GetFullPath(sceneJsonPath);
        if (!File.Exists(sceneJsonPath)) return false;
        var files = CollectFiles(Prepare(document), includeOriginals: false);
        try
        {
            if (!SameScene(await StorageSafety.ReadFileAsync(sceneJsonPath, cancellationToken), files["scene.json"])) return false;
            var directory = Path.GetDirectoryName(sceneJsonPath)!;
            foreach (var pair in files.Where(pair => pair.Key != "scene.json"))
            {
                var path = StorageSafety.ResolveUnder(directory, pair.Key);
                if (!File.Exists(path) || new FileInfo(path).Length != pair.Value.LongLength) return false;
                var existing = await StorageSafety.ReadFileAsync(path, cancellationToken);
                if (!pair.Value.AsSpan().SequenceEqual(existing)) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        { return false; }
    }

    private static bool SameScene(byte[] first, byte[] second)
    {
        var a = StorageSafety.ParseObject(first, "scene.json");
        var b = StorageSafety.ParseObject(second, "scene.json");
        if (a["board"] is JsonObject firstBoard) firstBoard.Remove("capturedUtc");
        if (b["board"] is JsonObject secondBoard) secondBoard.Remove("capturedUtc");
        return ExportTaskState.CanonicalJson(a).AsSpan().SequenceEqual(ExportTaskState.CanonicalJson(b));
    }

    public static async Task SaveAsync(BoardDocument document, string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = Prepare(document);
        var files = CollectFiles(prepared, includeOriginals: true);
        var hashes = new JsonObject();
        foreach (var pair in files) hashes[pair.Key] = StorageSafety.Hash(pair.Value);
        var manifest = new JsonObject
        {
            ["format"] = "whiteboard-archive", ["version"] = 1, ["sceneFile"] = "scene.json",
            ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O"), ["boardId"] = prepared.Id,
            ["title"] = prepared.Title, ["status"] = prepared.Status, ["files"] = hashes
        };
        foreach (var field in new[] { "lastModifiedUtc", "lastModifiedSource", "capturedUtc" })
            if (prepared.Scene["board"]?[field] is { } metadata) manifest[field] = metadata.DeepClone();
        files["manifest.json"] = JsonBytes(manifest);
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var pair in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entry = archive.CreateEntry(pair.Key, CompressionLevel.Optimal);
                        await using var entryStream = entry.Open();
                        await entryStream.WriteAsync(pair.Value, cancellationToken);
                    }
                }
                await file.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static async Task SaveJsonAsync(BoardDocument document, string sceneJsonPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = Prepare(document);
        var files = CollectFiles(prepared, includeOriginals: false);
        sceneJsonPath = Path.GetFullPath(sceneJsonPath);
        var directory = Path.GetDirectoryName(sceneJsonPath)!;
        Directory.CreateDirectory(directory);
        // Assets are content-addressed, so different scene JSON files can safely share this directory.
        // Every individual file and the scene itself is published only after a complete write.
        foreach (var pair in files.Where(pair => pair.Key != "scene.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = StorageSafety.ResolveUnder(directory, pair.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                // A previously exported file may have been truncated or changed by another app.
                // The in-memory resource is authoritative; atomically repair the output copy.
                if (new FileInfo(target).Length == pair.Value.LongLength)
                {
                    var current = await StorageSafety.ReadFileAsync(target, cancellationToken);
                    if (StorageSafety.Hash(current) == StorageSafety.Hash(pair.Value)) continue;
                }
            }
            await WriteAtomicAsync(target, pair.Value, cancellationToken);
        }
        await WriteAtomicAsync(sceneJsonPath, files["scene.json"], cancellationToken);
    }

    private static BoardDocument Prepare(BoardDocument source)
    {
        SceneValidation.Validate(source.Scene);
        var copy = new BoardDocument { Scene = (JsonObject)source.Scene.DeepClone(), SourcePath = source.SourcePath };
        foreach (var pair in source.Assets) copy.Assets[pair.Key] = pair.Value;
        foreach (var pair in source.Originals) copy.Originals[pair.Key] = pair.Value;
        AssetService.ExtractEmbeddedAssets(copy);
        SceneValidation.Validate(copy.Scene);
        return copy;
    }

    private static Dictionary<string, byte[]> CollectFiles(BoardDocument document, bool includeOriginals)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { ["scene.json"] = JsonBytes(document.Scene) };
        foreach (var pair in (JsonObject)document.Scene["assets"]!)
        {
            if (pair.Value is not JsonObject asset || asset["path"]?.GetValue<string>() is not { } path || !document.Assets.TryGetValue(path, out var bytes)) continue;
            path = StorageSafety.SafeRelativePath(path);
            if (!path.StartsWith("assets/", StringComparison.Ordinal)) throw new InvalidDataException("备份包资源必须位于 assets/ 目录。");
            files[path] = bytes;
        }
        if (includeOriginals)
            foreach (var pair in document.Originals) files["originals/" + StorageSafety.SafeRelativePath(pair.Key)] = pair.Value;
        long total = 0;
        if (files.Count > StorageSafety.MaxEntries - 1) throw new InvalidDataException("备份文件数量超过限制。");
        foreach (var pair in files)
            if (pair.Value.LongLength > StorageSafety.MaxFileBytes || (total += pair.Value.LongLength) > StorageSafety.MaxArchiveBytes)
                throw new InvalidDataException("备份数据超过大小限制。");
        return files;
    }

    private static byte[] JsonBytes(JsonObject node)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(node, JsonOptions);
        if (bytes.LongLength > StorageSafety.MaxFileBytes) throw new InvalidDataException("JSON 数据超过大小限制。");
        return bytes;
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
