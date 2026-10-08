using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WhiteboardExporter.Core;

public sealed class ImportService(IBoardNormalizer normalizer)
{
    public async Task<BoardDocument> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var bytes = await StorageSafety.ReadFileAsync(path, cancellationToken,
            extension is ".zip" or ".wbarchive" ? StorageSafety.MaxArchiveBytes : StorageSafety.MaxFileBytes);
        BoardDocument document;
        switch (extension)
        {
            case ".wbarchive":
                document = await ImportArchiveAsync(bytes, cancellationToken);
                break;
            case ".zip":
                var files = await StorageSafety.ReadZipAsync(bytes, cancellationToken);
                document = files.ContainsKey("manifest.json") && files.ContainsKey("scene.json")
                    ? await ImportArchiveAsync(bytes, cancellationToken)
                    : await ImportMicrosoftExportAsync(bytes, files, Path.GetFileNameWithoutExtension(path), cancellationToken);
                break;
            case ".json":
                document = new BoardDocument { Scene = StorageSafety.ParseObject(bytes, "scene.json") };
                SceneValidation.Validate(document.Scene);
                await LoadAdjacentAssetsAsync(document, Path.GetDirectoryName(path)!, cancellationToken);
                AssetService.ExtractEmbeddedAssets(document);
                break;
            case ".html":
            case ".htm":
                document = new BoardDocument { Scene = await normalizer.NormalizeAsync(StorageSafety.Utf8(bytes), null, StorageSafety.Hash(bytes)[..16], Path.GetFileNameWithoutExtension(path), cancellationToken) };
                SceneValidation.Validate(document.Scene);
                document.Originals["source/export.html"] = bytes;
                await LoadAdjacentAssetsAsync(document, Path.GetDirectoryName(path)!, cancellationToken, sourceReferences: true);
                AssetService.ExtractEmbeddedAssets(document);
                break;
            case ".whiteboard":
                document = PreserveNative(bytes, Path.GetFileNameWithoutExtension(path));
                break;
            default:
                throw new InvalidDataException("请选择微软导出 ZIP、.wbarchive、scene.json、HTML 或原生 .whiteboard 文件。");
        }
        cancellationToken.ThrowIfCancellationRequested();
        SceneValidation.Validate(document.Scene);
        document.SourcePath = path;
        return document;
    }

    private async Task<BoardDocument> ImportMicrosoftExportAsync(byte[] original, Dictionary<string, byte[]> files, string title, CancellationToken cancellationToken, string? id = null)
    {
        if (original.LongLength > StorageSafety.MaxFileBytes)
            throw new InvalidDataException("原始微软导出 ZIP 超过 128 MiB，无法纳入本版本的单文件备份限制。");
        var candidates = files.Where(pair => pair.Key.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || pair.Key.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            .Select(pair => new { pair.Key, Html = StorageSafety.Utf8(pair.Value) })
            .Where(pair => pair.Html.Contains("canvasContent", StringComparison.OrdinalIgnoreCase) || pair.Html.Contains("data-whiteboard", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 0) throw new InvalidDataException("ZIP 不包含可识别的微软白板画布 HTML，不能将评论或截图视为完整白板。");
        if (candidates.Length != 1) throw new InvalidDataException("ZIP 包含多个白板画布，请分别导出后导入。");
        var candidate = candidates[0];
        var commentsFile = files.FirstOrDefault(pair => Path.GetFileName(pair.Key).Contains("comment", StringComparison.OrdinalIgnoreCase) && pair.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        if (commentsFile.Key is null)
        {
            var jsonFiles = files.Where(pair => pair.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (jsonFiles.Length == 1) commentsFile = jsonFiles[0];
        }
        var comments = commentsFile.Key is null ? null : StorageSafety.Utf8(commentsFile.Value);
        var scene = await normalizer.NormalizeAsync(candidate.Html, comments, id ?? StorageSafety.Hash(original)[..16], title, cancellationToken);
        SceneValidation.Validate(scene);
        var document = new BoardDocument { Scene = scene };
        document.Originals["source/microsoft-export.zip"] = original;
        document.Originals["source/export.html"] = files[candidate.Key];
        if (commentsFile.Key is not null) document.Originals["source/comments.json"] = commentsFile.Value;
        LoadZipAssets(document, files, candidate.Key);
        AssetService.ExtractEmbeddedAssets(document);
        return document;
    }

    private async Task<BoardDocument> ImportArchiveAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var files = await StorageSafety.ReadZipAsync(bytes, cancellationToken);
        if (!files.TryGetValue("manifest.json", out var manifestBytes) || !files.TryGetValue("scene.json", out var sceneBytes))
            throw new InvalidDataException("备份包缺少 manifest.json 或 scene.json。");
        var manifest = StorageSafety.ParseObject(manifestBytes, "manifest.json");
        try
        {
            if (manifest["format"]?.GetValue<string>() != "whiteboard-archive" || manifest["version"]?.GetValue<int>() != 1 || manifest["sceneFile"]?.GetValue<string>() != "scene.json")
                throw new InvalidDataException("无法识别备份包格式或版本。");
            if (manifest["files"] is not JsonObject hashes) throw new InvalidDataException("备份包缺少文件完整性清单。");
            foreach (var pair in hashes)
            {
                var name = StorageSafety.SafeRelativePath(pair.Key);
                if (name == "manifest.json" || !files.TryGetValue(name, out var contents) || pair.Value?.GetValue<string>() != StorageSafety.Hash(contents))
                    throw new InvalidDataException($"备份包文件缺失或 SHA-256 校验失败：{name}");
            }
            if (files.Keys.Any(name => name != "manifest.json" && !hashes.ContainsKey(name)))
                throw new InvalidDataException("备份包包含未列入完整性清单的文件。");
            if (files.Keys.Any(name => name is not ("manifest.json" or "scene.json") &&
                !name.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("originals/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("备份包包含 SceneDocument v1 不支持的文件目录。");
        }
        catch (InvalidOperationException exception) { throw new InvalidDataException("备份包清单字段类型无效。", exception); }
        var document = new BoardDocument { Scene = StorageSafety.ParseObject(sceneBytes, "scene.json") };
        SceneValidation.Validate(document.Scene);
        foreach (var pair in files)
        {
            if (pair.Key.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) document.Assets[pair.Key] = pair.Value;
            if (pair.Key.StartsWith("originals/", StringComparison.OrdinalIgnoreCase)) document.Originals[pair.Key["originals/".Length..]] = pair.Value;
        }
        AssetService.ExtractEmbeddedAssets(document);
        if (document.Status == "failed") document = await RecoverFailedArchiveAsync(document, cancellationToken);
        else if (document.Status == "partial") document = await EnrichPartialArchiveAsync(document, cancellationToken);
        return document;
    }

    private async Task<BoardDocument> ReadArchivedSourceAsync(BoardDocument archived, CancellationToken cancellationToken)
    {
        if (archived.Originals.TryGetValue("source/microsoft-export.zip", out var originalZip))
        {
            var originalFiles = await StorageSafety.ReadZipAsync(originalZip, cancellationToken);
            return await ImportMicrosoftExportAsync(originalZip, originalFiles, archived.Title, cancellationToken, archived.Id);
        }
        archived.Originals.TryGetValue("source/comments.json", out var comments);
        var scene = await normalizer.NormalizeAsync(StorageSafety.Utf8(archived.Originals["source/export.html"]),
            comments is null ? null : StorageSafety.Utf8(comments), archived.Id, archived.Title, cancellationToken);
        SceneValidation.Validate(scene);
        var document = new BoardDocument { Scene = scene };
        AssetService.ExtractEmbeddedAssets(document);
        return document;
    }

    private async Task<BoardDocument> EnrichPartialArchiveAsync(BoardDocument archived, CancellationToken cancellationToken)
    {
        // A partial archive can already contain repaired geometry and captured media.
        // Re-reading preserved source may add identifiers, but must not overwrite it.
        if ((!archived.Originals.ContainsKey("source/microsoft-export.zip") && !archived.Originals.ContainsKey("source/export.html")) ||
            !AssetService.EnumerateElements(archived.Scene["elements"] as JsonArray).Any(element => element["nativeId"] is null || IsLegacyImage(element))) return archived;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var reparsed = await ReadArchivedSourceAsync(archived, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (reparsed.Status == "failed") throw new InvalidDataException("保留的源数据未提供可识别对象。");
            RestoreCapturedAssets(archived, reparsed);
            AssetService.ExtractEmbeddedAssets(reparsed);
            var enriched = new BoardDocument { Scene = (JsonObject)archived.Scene.DeepClone() };
            foreach (var asset in archived.Assets) enriched.Assets[asset.Key] = asset.Value;
            foreach (var original in archived.Originals) enriched.Originals[original.Key] = original.Value;
            var sourceElements = AssetService.EnumerateElements(reparsed.Scene["elements"] as JsonArray)
                .ToDictionary(element => element["id"]!.GetValue<string>(), StringComparer.Ordinal);
            var changed = 0;
            foreach (var element in AssetService.EnumerateElements(enriched.Scene["elements"] as JsonArray))
            {
                var id = element["id"]!.GetValue<string>();
                JsonObject? source = null;
                if (!IsSequenceId(id)) sourceElements.TryGetValue(id, out source);
                if (source is null && IsLegacyImage(element))
                {
                    var identity = element["nativeId"]?.GetValue<string>();
                    if (identity is null && element["sourceFragment"]?.GetValue<string>() is { Length: > 0 } fragment)
                    {
                        // The old diagnostic fragment still has the real native identity.
                        // Parse it through the same inert DOMParser, never guess element-N.
                        var parsedFragment = await normalizer.NormalizeAsync("<html><body><div id='canvasContent'>" + fragment + "</div></body></html>", null,
                            archived.Id, archived.Title, cancellationToken);
                        SceneValidation.Validate(parsedFragment);
                        var identities = AssetService.EnumerateElements(parsedFragment["elements"] as JsonArray)
                            .Where(item => string.Equals(item["nativeType"]?.GetValue<string>(), element["nativeType"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                            .Select(item => item["nativeId"]?.GetValue<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
                        if (identities.Length == 1) identity = identities[0];
                    }
                    if (identity is not null)
                    {
                        var matches = sourceElements.Values.Where(item => item["nativeId"]?.GetValue<string>() == identity &&
                            string.Equals(item["nativeType"]?.GetValue<string>(), element["nativeType"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (matches.Length == 1) source = matches[0];
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (source is null || !string.Equals(element["nativeType"]?.GetValue<string>(), source["nativeType"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase)) continue;
                var nativeId = source["nativeId"]?.GetValue<string>();
                if (element["nativeId"]?.GetValue<string>() is { } existingIdentity && nativeId is not null && existingIdentity != nativeId) continue;
                if (element["nativeId"] is null && !string.IsNullOrWhiteSpace(nativeId))
                { element["nativeId"] = nativeId; changed++; }
                if (IsLegacyImage(element) && source["kind"]?.GetValue<string>() == "image")
                { UpgradeLegacyImage(enriched, reparsed, element, source); changed++; }
            }
            if (changed > 0) AddRecoveryDiagnostic(enriched, "ARCHIVE_METADATA_REFRESHED", "info", "已从保留源数据补充原生标识，并升级旧版图片占位；其余场景、图片及来源哈希保持不变。");
            AssetService.RefreshDiagnostics(enriched);
            SceneValidation.Validate(enriched.Scene);
            return enriched;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException or
            FormatException or DecoderFallbackException or System.Text.Json.JsonException or NotSupportedException or TimeoutException)
        {
            AddRecoveryDiagnostic(archived, "ARCHIVE_METADATA_REFRESH_FAILED", "warning", "无法从保留的源数据补充原生对象标识；已有场景及图片保持不变。");
            return archived;
        }
    }

    private static bool IsSequenceId(string id) => id.StartsWith("element-", StringComparison.Ordinal) && int.TryParse(id.AsSpan(8), out _);

    private static bool IsLegacyImage(JsonObject element) => element["kind"]?.GetValue<string>() == "unknown" &&
        element["nativeType"]?.GetValue<string>()?.ToLowerInvariant() is "azureimage" or "fluidimage";

    private static void UpgradeLegacyImage(BoardDocument archived, BoardDocument reparsed, JsonObject element, JsonObject source)
    {
        var sourceAssets = (JsonObject)reparsed.Scene["assets"]!;
        var targetAssets = (JsonObject)archived.Scene["assets"]!;
        var references = new HashSet<string>(StringComparer.Ordinal);
        if (source["assetId"]?.GetValue<string>() is { } direct) references.Add(direct);
        foreach (var field in new[] { "html", "svg" })
            if (source[field]?.GetValue<string>() is { } markup)
                foreach (Match match in Regex.Matches(markup, "wbasset:([A-Za-z0-9_.-]+)")) references.Add(match.Groups[1].Value);
        var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assetId in references)
        {
            if (sourceAssets[assetId] is not JsonObject metadata) continue;
            var sourceIdentity = SourceIdentity(metadata);
            var path = metadata["path"]?.GetValue<string>();
            var existing = targetAssets.FirstOrDefault(entry => entry.Value is JsonObject asset &&
                ((sourceIdentity is not null && string.Equals(SourceIdentity(asset), sourceIdentity, StringComparison.OrdinalIgnoreCase)) ||
                 (path is not null && asset["path"]?.GetValue<string>() == path)));
            if (existing.Key is not null)
            {
                mapped[assetId] = existing.Key;
                if (existing.Value is JsonObject existingAsset &&
                    (existingAsset["path"]?.GetValue<string>() is not { } oldPath || !archived.Assets.ContainsKey(oldPath)) &&
                    path is not null && reparsed.Assets.TryGetValue(path, out var restoredBytes))
                    AssetService.AddAsset(archived, existing.Key, restoredBytes, metadata["mime"]?.GetValue<string>());
                continue;
            }
            var newId = "reparsed-" + StorageSafety.Hash(Encoding.UTF8.GetBytes(element["id"]!.GetValue<string>() + "\0" + assetId))[..24];
            while (targetAssets.ContainsKey(newId)) newId += "-copy";
            targetAssets[newId] = metadata.DeepClone();
            if (path is not null && reparsed.Assets.TryGetValue(path, out var bytes)) AssetService.AddAsset(archived, newId, bytes, metadata["mime"]?.GetValue<string>());
            mapped[assetId] = newId;
        }
        element["kind"] = "image";
        foreach (var field in new[] { "html", "svg", "text" })
        {
            if (source[field]?.GetValue<string>() is not { } value) { element.Remove(field); continue; }
            element[field] = Regex.Replace(value, "wbasset:([A-Za-z0-9_.-]+)", match =>
                mapped.TryGetValue(match.Groups[1].Value, out var replacement) ? "wbasset:" + replacement : match.Value);
        }
        if (source["assetId"]?.GetValue<string>() is { } sourceAssetId && mapped.TryGetValue(sourceAssetId, out var targetAssetId)) element["assetId"] = targetAssetId;
        else element.Remove("assetId");
        var diagnostics = (JsonObject)archived.Scene["diagnostics"]!;
        var items = (JsonArray)diagnostics["items"]!;
        var id = element["id"]!.GetValue<string>();
        for (var index = items.Count - 1; index >= 0; index--)
            if (items[index] is JsonObject item && item["elementId"]?.GetValue<string>() == id && item["code"]?.GetValue<string>() == "UNSUPPORTED_TYPE") items.RemoveAt(index);
        var sourceId = source["id"]!.GetValue<string>();
        foreach (var item in ((JsonArray)reparsed.Scene["diagnostics"]!["items"]!).OfType<JsonObject>().Where(item => item["elementId"]?.GetValue<string>() == sourceId))
        {
            var copy = (JsonObject)item.DeepClone();
            copy["elementId"] = id;
            if (copy["assetId"]?.GetValue<string>() is { } assetId && mapped.TryGetValue(assetId, out var replacement)) copy["assetId"] = replacement;
            if (!items.OfType<JsonObject>().Any(old => old["elementId"]?.GetValue<string>() == id && old["code"]?.GetValue<string>() == copy["code"]?.GetValue<string>())) items.Add(copy);
        }
        if (diagnostics["unsupported"]!.GetValue<int>() > 0) diagnostics["unsupported"] = diagnostics["unsupported"]!.GetValue<int>() - 1;
        diagnostics["supported"] = diagnostics["supported"]!.GetValue<int>() + 1;
    }

    private async Task<BoardDocument> RecoverFailedArchiveAsync(BoardDocument archived, CancellationToken cancellationToken)
    {
        // Parsing behavior improves between releases. A previous failed scene is a cache,
        // while the integrity-checked, preserved Microsoft source remains authoritative.
        if (!archived.Originals.ContainsKey("source/microsoft-export.zip") && !archived.Originals.ContainsKey("source/export.html")) return archived;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var reparsed = await ReadArchivedSourceAsync(archived, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            reparsed.Scene["board"] = archived.Scene["board"]!.DeepClone();
            foreach (var pair in archived.Originals) reparsed.Originals[pair.Key] = pair.Value;
            foreach (var pair in archived.Assets) reparsed.Assets.TryAdd(pair.Key, pair.Value);
            RestoreCapturedAssets(archived, reparsed);
            AssetService.ExtractEmbeddedAssets(reparsed);
            AddRecoveryDiagnostic(reparsed, "ARCHIVE_REPARSED_SOURCE", "info", "已使用当前解析器重新解析备份中保留的微软源数据，原始文件保持不变。");
            SceneValidation.Validate(reparsed.Scene);
            return reparsed;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException or
            FormatException or DecoderFallbackException or System.Text.Json.JsonException or NotSupportedException or TimeoutException)
        {
            // Never turn cancellation, a corrupt source, or a second parser failure into success.
            // Keep the original failed scene and all source bytes so a later version can try again.
            AddRecoveryDiagnostic(archived, "ARCHIVE_REPARSE_FAILED", "warning", "当前解析器仍无法重新解析保留的源数据；原始备份已保留，可继续导出原始备份。");
            return archived;
        }
    }

    private static void RestoreCapturedAssets(BoardDocument archived, BoardDocument reparsed)
    {
        var previous = (JsonObject)archived.Scene["assets"]!;
        foreach (var pair in (JsonObject)reparsed.Scene["assets"]!)
        {
            if (pair.Value is not JsonObject asset || asset["dataUrl"] is not null) continue;
            if (asset["path"]?.GetValue<string>() is { } currentPath && reparsed.Assets.ContainsKey(currentPath)) continue;
            var sourceHash = SourceIdentity(asset);
            if (sourceHash is null) continue;
            // Match provenance, not position or sequence number: parser changes can renumber images.
            // Captured private/blob URLs are redacted after acquisition, retaining only their
            // SHA-256 identity. The freshly parsed HTML can still contain that original URL.
            foreach (var old in previous.Select(entry => entry.Value).OfType<JsonObject>())
            {
                if (!string.Equals(SourceIdentity(old), sourceHash, StringComparison.OrdinalIgnoreCase) || old["path"]?.GetValue<string>() is not { } oldPath ||
                    !archived.Assets.TryGetValue(oldPath, out var bytes)) continue;
                AssetService.AddAsset(reparsed, pair.Key, bytes, old["mime"]?.GetValue<string>() ?? asset["mime"]?.GetValue<string>());
                if (SourceHash(old) is { } redactedHash)
                {
                    asset["sourceHash"] = redactedHash;
                    asset.Remove("source");
                }
                break;
            }
        }
    }

    private static string? SourceIdentity(JsonObject asset) => SourceHash(asset) ??
        (asset["source"]?.GetValue<string>() is { Length: > 0 } source ? StorageSafety.Hash(Encoding.UTF8.GetBytes(source)) : null);

    private static string? SourceHash(JsonObject asset) =>
        asset["sourceHash"] is JsonValue value && value.TryGetValue<string>(out var hash) && hash.Length == 64 && hash.All(Uri.IsHexDigit)
            ? hash.ToLowerInvariant() : null;

    private static void AddRecoveryDiagnostic(BoardDocument document, string code, string severity, string message)
    {
        var items = (JsonArray)document.Scene["diagnostics"]!["items"]!;
        for (var index = items.Count - 1; index >= 0; index--)
            if (items[index] is JsonObject item && item["code"]?.GetValue<string>() == code) items.RemoveAt(index);
        items.Add(new JsonObject { ["code"] = code, ["severity"] = severity, ["message"] = message });
    }

    private static void LoadZipAssets(BoardDocument document, Dictionary<string, byte[]> files, string htmlPath)
    {
        var prefix = htmlPath.Contains('/') ? htmlPath[..(htmlPath.LastIndexOf('/') + 1)] : "";
        foreach (var pair in (JsonObject)document.Scene["assets"]!)
        {
            if (pair.Value is not JsonObject asset || asset["dataUrl"] is not null) continue;
            var candidates = new[] { asset["source"]?.GetValue<string>(), asset["path"]?.GetValue<string>() };
            foreach (var reference in candidates)
            {
                var relative = LocalReference(reference);
                if (relative is null) continue;
                var relativeToHtml = StorageSafety.SafeRelativePath(prefix + relative);
                if (files.TryGetValue(relativeToHtml, out var data) || files.TryGetValue(relative, out data))
                {
                    AssetService.AddAsset(document, pair.Key, data, asset["mime"]?.GetValue<string>() ?? AssetService.MimeForPath(relative));
                    break;
                }
            }
        }
    }

    private static async Task LoadAdjacentAssetsAsync(BoardDocument document, string directory, CancellationToken cancellationToken, bool sourceReferences = false)
    {
        long total = 0;
        foreach (var pair in (JsonObject)document.Scene["assets"]!)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pair.Value is not JsonObject asset || asset["dataUrl"] is not null || asset["source"]?.GetValue<string>()?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true) continue;
            var reference = sourceReferences ? asset["source"]?.GetValue<string>() ?? asset["path"]?.GetValue<string>() : asset["path"]?.GetValue<string>();
            var relative = LocalReference(reference);
            if (relative is null) continue;
            var assetPath = StorageSafety.ResolveUnder(directory, relative);
            if (!File.Exists(assetPath)) continue;
            var data = await StorageSafety.ReadFileAsync(assetPath, cancellationToken);
            if ((total += data.LongLength) > StorageSafety.MaxArchiveBytes) throw new InvalidDataException("资源总大小超过限制。");
            AssetService.AddAsset(document, pair.Key, data, asset["mime"]?.GetValue<string>() ?? AssetService.MimeForPath(relative));
        }
    }

    private static string? LocalReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        if (Uri.TryCreate(reference, UriKind.Absolute, out _)) return null;
        var path = Uri.UnescapeDataString(reference.Split('#')[0].Split('?')[0]);
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        return StorageSafety.SafeRelativePath(path);
    }

    private static BoardDocument PreserveNative(byte[] bytes, string title)
    {
        var document = new BoardDocument
        {
            Scene = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["board"] = new JsonObject { ["id"] = StorageSafety.Hash(bytes)[..16], ["title"] = title },
                ["bounds"] = new JsonObject { ["x"] = 0.0, ["y"] = 0.0, ["width"] = 1200.0, ["height"] = 800.0 },
                ["background"] = "#ffffff", ["elements"] = new JsonArray(), ["assets"] = new JsonObject(),
                ["diagnostics"] = new JsonObject
                {
                    ["status"] = "failed", ["total"] = 1, ["supported"] = 0, ["unsupported"] = 1,
                    ["missingAssets"] = new JsonArray(),
                    ["items"] = new JsonArray(new JsonObject { ["code"] = "opaque-native-file", ["message"] = "已原样保留 .whiteboard 文件。当前版本无法直接解码任意 Fluid 白板文件，请使用微软官方 HTML/ZIP 导出后重绘。" })
                }
            }
        };
        document.Originals["source/native.whiteboard"] = bytes;
        return document;
    }
}
