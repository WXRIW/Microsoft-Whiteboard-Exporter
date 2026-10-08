using System.IO;
using System.Text.Json.Nodes;
using WhiteboardExporter.Core;

namespace WhiteboardExporter.App;

public partial class MainWindow
{
    internal async Task<IReadOnlyList<string>> TestAccountIsolationAsync(BoardDocument template, string directory)
    {
        var checks = new List<string>();
        void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        const string sharedId = "00000000-0000-0000-0000-000000000201";
        BoardDocument Fixture(string title, string color, ExportAccount? account = null)
        {
            var document = new BoardDocument { Scene = (JsonObject)template.Scene.DeepClone() };
            document.Scene["board"] = new JsonObject { ["id"] = sharedId, ["title"] = title };
            if (account is not null) document.Scene["board"]!["account"] = account.ToJson();
            document.Scene["bounds"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 320, ["height"] = 160 };
            document.Scene["background"] = "#ffffff";
            document.Scene["assets"] = new JsonObject();
            document.Scene["elements"] = new JsonArray(new JsonObject
            {
                ["id"] = "account-marker", ["kind"] = "shape", ["z"] = 0,
                ["transform"] = new JsonArray(1, 0, 0, 1, 0, 0),
                ["bounds"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 320, ["height"] = 160 },
                ["svg"] = $"<rect width='320' height='160' fill='{color}'/><path d='M20 20L300 140M300 20L20 140' stroke='#ffffff' stroke-width='12'/>"
            });
            document.Scene["diagnostics"] = new JsonObject
            {
                ["status"] = "complete", ["total"] = 1, ["supported"] = 1, ["unsupported"] = 0,
                ["missingAssets"] = new JsonArray(), ["items"] = new JsonArray()
            };
            return document;
        }

        var legacyRoot = Path.Combine(directory, "legacy-account-fixtures");
        var legacyFolders = new[]
        {
            Path.Combine(legacyRoot, "旧账号 A", "白板 A__" + sharedId),
            Path.Combine(legacyRoot, "旧账号 B", "白板 B__" + sharedId)
        };
        var legacySources = legacyFolders.Select(folder => Path.Combine(folder, "scene.json")).ToArray();
        await ArchiveService.SaveJsonAsync(Fixture("旧账号 A 的白板", "#dc2626"), legacySources[0]);
        await ArchiveService.SaveJsonAsync(Fixture("旧账号 B 的白板", "#2563eb"), legacySources[1]);
        var sourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in legacySources) sourceHashes[path] = await HashFileAsync(path, CancellationToken.None);

        var previousBoards = boards.ToArray(); var previousFailed = failed.ToArray();
        var previousCurrent = current; var previousSelection = BoardList.SelectedItem as BoardItem;
        var previousOutput = OutputBox.Text;
        var formatBoxes = new[] { FormatArchive, FormatJson, FormatPdf, FormatPng, FormatJpeg, FormatSvg, FormatHtml };
        var previousFormats = formatBoxes.Select(box => box.IsChecked).ToArray();
        try
        {
            foreach (var box in formatBoxes) box.IsChecked = false;
            FormatArchive.IsChecked = FormatJson.IsChecked = FormatPdf.IsChecked = FormatPng.IsChecked = true;
            await OpenLibraryAsync(legacyRoot);
            var legacyItems = legacyFolders.Select(folder => boards.Single(item => item.LibraryEntry?.DirectoryPath == folder)).ToArray();
            foreach (var item in legacyItems) await LoadLibraryDocumentAsync(item, CancellationToken.None);
            Require(legacyItems.All(item => item.Id == sharedId && item.Document is not null && item.Account?.Source == "local-directory") &&
                legacyItems.Select(item => BoardAccount(item).Key).Distinct().Count() == 2,
                "缺少账号清单和场景账号的旧 A/B 目录按目录上下文绑定独立账号，同 ID 保留两条");
            var legacyOutput = Path.Combine(directory, "legacy-account-output");
            OutputBox.Text = legacyOutput;
            await ExportBatchAsync(legacyItems, CancellationToken.None);
            var accountFolders = Directory.GetDirectories(legacyOutput);
            var exportedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pngHashes = new HashSet<string>(StringComparer.Ordinal);
            var namespaces = new HashSet<string>(StringComparer.Ordinal);
            var valid = accountFolders.Length == 2 && legacyItems.All(item => item.ExportError is null);
            foreach (var folder in accountFolders)
            {
                var account = await AccountDirectoryService.ReadAccountAsync(folder);
                if (account is null) { valid = false; continue; }
                namespaces.Add(account.Key);
                var owner = legacyItems.SingleOrDefault(item => BoardAccount(item).Key == account.Key);
                var state = await ReadStateAsync(Path.Combine(folder, "export-state.json"), CancellationToken.None);
                var record = state[EntryKey(sharedId)];
                var paths = (record?["files"] as JsonArray)?.Select(node => node!.GetValue<string>()).ToArray() ?? [];
                valid &= owner is not null && record?["accountKey"]?.GetValue<string>() == account.Key &&
                    record?["status"]?.GetValue<string>() == "complete" && paths.Length == 4;
                foreach (var path in paths)
                {
                    valid &= exportedPaths.Add(path) && File.Exists(path) &&
                        Path.GetDirectoryName(Path.GetDirectoryName(path)!)!.Equals(folder, StringComparison.OrdinalIgnoreCase);
                    var extension = Path.GetExtension(path).ToLowerInvariant();
                    if (extension is ".wbarchive" or ".json")
                    {
                        var restored = await importer.ImportAsync(path);
                        valid &= restored.Id == sharedId && restored.Title == owner?.Title && ReadDocumentAccount(restored)?.Key == account.Key;
                    }
                    else if (extension == ".png") pngHashes.Add(await HashFileAsync(path, CancellationToken.None));
                }
            }
            Require(valid && namespaces.Count == 2 && exportedPaths.Count == 8 && pngHashes.Count == 2,
                "旧 A/B 目录同 ID 批量导出各自的 PDF、PNG、备份、JSON 和任务状态，颜色内容不互相覆盖");
            var sourcesUnchanged = true;
            foreach (var path in legacySources)
                sourcesUnchanged &= sourceHashes[path] == await HashFileAsync(path, CancellationToken.None) &&
                    (JsonNode.Parse(await File.ReadAllTextAsync(path))?["board"]?["account"] is null);
            Require(sourcesUnchanged && Directory.GetFiles(legacyRoot, "account.json", SearchOption.AllDirectories).Length == 0,
                "旧目录导入和再次导出不写回原始 JSON 或在源账号目录新增清单");

            var conflictRoot = Path.Combine(directory, "conflict-account-fixtures");
            var manifestAccount = ExportAccount.FromLabel("清单账号 A");
            var archiveAccount = ExportAccount.FromLabel("备份账号 B");
            var manifestFolder = await AccountDirectoryService.ResolveAsync(conflictRoot, manifestAccount);
            var conflictFolder = Path.Combine(manifestFolder, "冲突白板__" + sharedId);
            Directory.CreateDirectory(conflictFolder);
            var archivePath = Path.Combine(conflictFolder, "board.wbarchive");
            await ArchiveService.SaveAsync(Fixture("备份 B 的白板", "#059669", archiveAccount), archivePath);
            var archiveHash = await HashFileAsync(archivePath, CancellationToken.None);
            await OpenLibraryAsync(conflictRoot);
            var conflicted = boards.Single(item => item.LibraryEntry?.DirectoryPath == conflictFolder);
            await LoadLibraryDocumentAsync(conflicted, CancellationToken.None);
            Require(conflicted.Account?.Key == archiveAccount.Key && conflicted.Document is { } document &&
                ReadDocumentAccount(document)?.Key == archiveAccount.Key && !string.IsNullOrWhiteSpace(conflicted.LibraryWarning),
                "账号清单 A 与备份 B 冲突时保留备份 B 的账号并显示归属警告");
            var conflictOutput = Path.Combine(directory, "conflict-account-output");
            OutputBox.Text = conflictOutput;
            await ExportBatchAsync([conflicted], CancellationToken.None);
            var resultFolders = Directory.GetDirectories(conflictOutput);
            var correctOwner = resultFolders.Length == 1 && conflicted.ExportError is null;
            if (resultFolders.Length == 1)
            {
                var account = await AccountDirectoryService.ReadAccountAsync(resultFolders[0]);
                var state = await ReadStateAsync(Path.Combine(resultFolders[0], "export-state.json"), CancellationToken.None);
                var record = state[EntryKey(sharedId)];
                var paths = (record?["files"] as JsonArray)?.Select(node => node!.GetValue<string>()).ToArray() ?? [];
                correctOwner &= account?.Key == archiveAccount.Key && record?["accountKey"]?.GetValue<string>() == archiveAccount.Key &&
                    record?["status"]?.GetValue<string>() == "complete" && paths.Length == 4;
                foreach (var path in paths.Where(path => Path.GetExtension(path) is ".wbarchive" or ".json"))
                    correctOwner &= ReadDocumentAccount(await importer.ImportAsync(path))?.Key == archiveAccount.Key;
            }
            Require(correctOwner && archiveHash == await HashFileAsync(archivePath, CancellationToken.None) &&
                (await AccountDirectoryService.ReadAccountAsync(manifestFolder))?.Key == manifestAccount.Key &&
                !string.IsNullOrWhiteSpace(conflicted.LibraryWarning),
                "冲突备份重新导出只归入 B 的账号目录且保留警告，源备份与 A 的清单保持原值");
            return checks;
        }
        finally
        {
            OutputBox.Text = previousOutput;
            for (var index = 0; index < formatBoxes.Length; index++) formatBoxes[index].IsChecked = previousFormats[index];
            failed.Clear(); failed.AddRange(previousFailed);
            selecting = true;
            try { boards.Clear(); foreach (var item in previousBoards) boards.Add(item); BoardList.SelectedItem = previousSelection; }
            finally { selecting = false; }
            if (previousCurrent is not null) await ShowDocumentAsync(previousCurrent);
            else { current = null; currentItem = previousSelection; currentPdf = currentImage = offlineVisualPath = null; ClearMediaViews(); UpdatePreviewActions(); }
        }
    }
}
