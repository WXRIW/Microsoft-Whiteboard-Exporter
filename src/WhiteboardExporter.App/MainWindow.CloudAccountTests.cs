using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhiteboardExporter.App.Cloud;
using WhiteboardExporter.Core;

namespace WhiteboardExporter.App;

public partial class MainWindow
{
    // Exercises the production gallery binding without navigating a Microsoft page.
    internal async Task<IReadOnlyList<string>> TestCloudAccountBindingAsync(BoardDocument template, string directory)
    {
        var checks = new List<string>();
        void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        const string firstId = "00000000-0000-0000-0000-000000000301";
        const string secondId = "00000000-0000-0000-0000-000000000302";
        const string thirdId = "00000000-0000-0000-0000-000000000303";
        var accountA = ExportAccount.FromLabel("模拟个人账号 A", "msa");
        var accountB = ExportAccount.FromLabel("模拟组织账号 B", "aad");
        var manual = ExportAccount.FromLabel("尚未识别时手工填写的账号 M");
        var fresh = new[] { new CloudBoard(firstId, "当前 A 的第一张"), new CloudBoard(secondId, "当前 A 的第二张") };
        BoardItem Live(string id, ExportAccount? account = null) => new()
        { Id = id, Title = "旧列表标题", Account = account, Cloud = new CloudBoard(id, "旧列表标题"), Subtitle = "微软云端" };
        BoardDocument Document(string id, ExportAccount? account)
        {
            var document = new BoardDocument { Scene = (JsonObject)template.Scene.DeepClone() };
            document.Scene["board"] = new JsonObject { ["id"] = id, ["title"] = "保留的旧本地内容" };
            if (account is not null) document.Scene["board"]!["account"] = account.ToJson();
            document.Scene["background"] = "#d62828";
            foreach (var asset in template.Assets) document.Assets[asset.Key] = asset.Value;
            foreach (var original in template.Originals) document.Originals[original.Key] = original.Value;
            document.Originals["responses/manual-only.json"] = Encoding.UTF8.GetBytes("{\"source\":\"old-manual-namespace\"}");
            return document;
        }
        void Reset(params BoardItem[] items)
        {
            var wasSelecting = selecting; selecting = true;
            try { boards.Clear(); foreach (var item in items) boards.Add(item); }
            finally { selecting = wasSelecting; }
            current = null; currentItem = null; currentPdf = currentImage = offlineVisualPath = null;
        }
        string Snapshot()
        {
            var rows = new JsonArray();
            foreach (var item in boards)
                rows.Add(new JsonObject
                {
                    ["object"] = RuntimeHelpers.GetHashCode(item), ["id"] = item.Id, ["title"] = item.Title,
                    ["account"] = item.Account?.ToJson(), ["cloud"] = JsonSerializer.Serialize(item.Cloud),
                    ["document"] = item.Document?.Scene.ToJsonString(),
                    ["documentObject"] = item.Document is null ? null : JsonValue.Create(RuntimeHelpers.GetHashCode(item.Document)),
                    ["pdf"] = item.PdfPath, ["image"] = item.ImagePath, ["html"] = item.HtmlPath, ["svg"] = item.SvgPath,
                    ["subtitle"] = item.Subtitle, ["error"] = item.ExportError,
                    ["retry"] = string.Join(";", item.RetryOriginals.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + Convert.ToHexString(pair.Value)))
                });
            return new JsonObject
            {
                ["rows"] = rows, ["selected"] = string.Join(",", BoardList.SelectedItems.Cast<BoardItem>().Select(RuntimeHelpers.GetHashCode)),
                ["current"] = current?.Scene.ToJsonString(), ["currentItem"] = currentItem is null ? null : JsonValue.Create(RuntimeHelpers.GetHashCode(currentItem)),
                ["pdf"] = currentPdf, ["image"] = currentImage, ["visual"] = offlineVisualPath,
                ["title"] = BoardTitle.Text, ["metadata"] = BoardMetadataText.Text, ["diagnostics"] = DiagnosticsText.Text
            }.ToJsonString();
        }
        bool RejectWithoutChanges(IReadOnlyList<CloudBoard> gallery, ExportAccount account, params BoardItem[] selected)
        {
            var snapshot = Snapshot();
            try { BindFreshCloudBoards(gallery, account, selected); return false; }
            catch (InvalidOperationException) { return Snapshot() == snapshot; }
        }

        var previousBoards = boards.ToArray(); var previousFailed = failed.ToArray();
        var previousCurrent = current; var previousItem = currentItem;
        var previousSelected = BoardList.SelectedItems.Cast<BoardItem>().ToArray();
        var previousPdf = currentPdf; var previousImage = currentImage; var previousVisual = offlineVisualPath;
        var previousTitle = BoardTitle.Text; var previousMetadata = BoardMetadataText.Text; var previousDiagnostics = DiagnosticsText.Text;
        try
        {
            var unknown = Live(firstId); var unselected = Live(secondId);
            unknown.RetryOriginals["responses/unknown.json"] = [1, 2, 3];
            unknown.PdfPath = "old-unknown.pdf"; unknown.ImagePath = "old-unknown.png";
            unknown.HtmlPath = "old-unknown.html"; unknown.SvgPath = "old-unknown.svg"; unknown.ExportError = "旧失败";
            Reset(unknown, unselected); SelectQuietly(unknown);
            var neededRetryBinding = NeedsCloudAccountBindingForRetry(boards);
            var mapped = BindFreshCloudBoards([.. fresh, new CloudBoard(thirdId, "新出现的第三张")], accountA, [unknown]);
            Require(neededRetryBinding && !NeedsCloudAccountBindingForRetry(boards) && ReferenceEquals(mapped[0], unknown) &&
                boards.Count == 3 && boards.All(item => item.Cloud is not null && item.Account?.Key == accountA.Key) &&
                ReferenceEquals(boards.Single(item => item.Id == secondId), unselected) &&
                unknown.RetryOriginals.Count == 0 && unknown.PdfPath is null && unknown.ImagePath is null &&
                unknown.HtmlPath is null && unknown.SvgPath is null && unknown.ExportError is null,
                "未知账号升级时整份 fresh 列表统一绑定当前账号，未选行原位保留，旧缓存清除且重试不再落入 Local");

            var temporaryManual = Live(firstId, manual);
            temporaryManual.RetryOriginals["responses/manual.json"] = [9]; temporaryManual.PdfPath = "manual.pdf";
            Reset(temporaryManual); SelectQuietly(temporaryManual);
            mapped = BindFreshCloudBoards([fresh[0]], accountA, [temporaryManual]);
            Require(ReferenceEquals(mapped[0], temporaryManual) && boards.Count == 1 && temporaryManual.Account?.Key == accountA.Key &&
                temporaryManual.Document is null && temporaryManual.RetryOriginals.Count == 0 && temporaryManual.PdfPath is null &&
                ReferenceEquals(BoardList.SelectedItem, temporaryManual) && ReferenceEquals(currentItem, temporaryManual),
                "未下载手工 M 可升级已识别 A，不误报跨账号且不增加同 ID 条目");

            var historyRoot = Path.Combine(directory, "saved-manual-history");
            var historyAccount = await AccountDirectoryService.ResolveAsync(historyRoot, manual);
            var historyBoard = Path.Combine(historyAccount, "旧白板__" + firstId);
            var historyDocument = Document(firstId, manual);
            await ArchiveService.SaveAsync(historyDocument, Path.Combine(historyBoard, "board.wbarchive"));
            await ArchiveService.SaveJsonAsync(historyDocument, Path.Combine(historyBoard, "scene.json"));
            await File.WriteAllTextAsync(Path.Combine(historyBoard, "board.pdf"), "old PDF bytes");
            await File.WriteAllTextAsync(Path.Combine(historyBoard, "board.png"), "old image bytes");
            await File.WriteAllTextAsync(Path.Combine(historyAccount, "export-state.json"), new JsonObject
            { [EntryKey(firstId)] = new JsonObject { ["accountKey"] = manual.Key, ["status"] = "failed", ["outputFolder"] = historyBoard } }.ToJsonString());
            var historyFiles = Directory.GetFiles(historyRoot, "*", SearchOption.AllDirectories);
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var times = historyFiles.ToDictionary(path => path, File.GetLastWriteTimeUtc, StringComparer.OrdinalIgnoreCase);
            foreach (var path in historyFiles) hashes[path] = await HashFileAsync(path, CancellationToken.None);
            var manualItem = Live(firstId, manual); manualItem.Document = historyDocument;
            manualItem.PdfPath = Path.Combine(historyBoard, "board.pdf"); manualItem.ImagePath = Path.Combine(historyBoard, "board.png");
            manualItem.RetryOriginals["responses/manual-only.json"] = [7, 8, 9];
            var unknownSaved = Live(secondId); unknownSaved.Document = Document(secondId, null);
            var manualScene = historyDocument.Scene.ToJsonString(); var unknownScene = unknownSaved.Document.Scene.ToJsonString();
            Reset(manualItem, unknownSaved); SelectQuietly(manualItem);
            current = historyDocument; currentItem = manualItem; currentPdf = manualItem.PdfPath; currentImage = manualItem.ImagePath;
            offlineVisualPath = "old-manual.html"; SceneView.Visibility = Visibility.Visible;
            ImagePreview.Source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            mapped = BindFreshCloudBoards(fresh, accountA, [manualItem, unknownSaved]);
            Require(boards.Count == 4 && manualItem.Cloud is null && unknownSaved.Cloud is null &&
                manualItem.Account?.Key == manual.Key && unknownSaved.Account is null &&
                ReferenceEquals(manualItem.Document, historyDocument) && historyDocument.Scene.ToJsonString() == manualScene &&
                unknownSaved.Document.Scene.ToJsonString() == unknownScene && manualItem.Subtitle.Contains("本地备份") &&
                mapped.All(item => item.Cloud is not null && item.Account?.Key == accountA.Key && item.Document is null &&
                    item.RetryOriginals.Count == 0 && item.PdfPath is null && item.ImagePath is null && item.HtmlPath is null && item.SvgPath is null),
                "已有 M 和未知账号文档保留原归属成为本地备份，新 A 云端条目不继承文档、原始响应或媒体缓存");
            Require(ReferenceEquals(BoardList.SelectedItem, mapped[0]) && BoardList.SelectedItems.Cast<BoardItem>().SequenceEqual(mapped) &&
                current is null && ReferenceEquals(currentItem, mapped[0]) && currentPdf is null && currentImage is null && offlineVisualPath is null &&
                ImagePreview.Source is null && SceneView.Visibility == Visibility.Hidden && !OriginalPreviewButton.IsEnabled &&
                BoardTitle.Text == fresh[0].Title && BoardMetadataText.Text.Contains(accountA.DisplayName),
                "账号升级同步选中 A 并清空旧原始、PDF 和图片预览，采集失败也不会显示 M 为 A");
            ApplyCloudBoards(fresh, accountA); ApplyCloudBoards(fresh, accountA);
            Require(boards.Count == 4 && boards.Count(item => item.Cloud is not null) == 2 &&
                mapped.All(item => boards.Contains(item)) && manualItem.Cloud is null && unknownSaved.Cloud is null,
                "识别为 A 后重复刷新不增加 live 条目且保留独立本地 M 备份");
            var unchanged = historyFiles.Length == Directory.GetFiles(historyRoot, "*", SearchOption.AllDirectories).Length;
            foreach (var path in historyFiles) unchanged &= hashes[path] == await HashFileAsync(path, CancellationToken.None) && times[path] == File.GetLastWriteTimeUtc(path);
            Require(unchanged && Directory.GetDirectories(historyRoot).Length == 1,
                "升级和重复刷新保持 M 的备份、JSON、媒体、账号清单及失败任务哈希和修改时间不变，不搬文件");

            var known = Live(firstId, accountA); known.Document = Document(firstId, accountA); known.RetryOriginals["responses/a.json"] = [1];
            Reset(known); SelectQuietly(known);
            var conflictChecks = RejectWithoutChanges([fresh[0]], accountB, known);
            var otherPersonal = ExportAccount.FromLabel("另一个已确认个人账号", "msa");
            conflictChecks &= RejectWithoutChanges([fresh[0]], otherPersonal, known);
            var partialFirst = Live(firstId); var partialConflict = Live(secondId, accountB);
            Reset(partialFirst, partialConflict); SelectQuietly(partialFirst);
            conflictChecks &= RejectWithoutChanges(fresh, accountA, partialFirst, partialConflict);
            Require(conflictChecks,
                "明确个人或组织账号不同 Key 始终拒绝，即使共享同 ID；第二条账号冲突不会先改第一条或预览");

            var present = Live(firstId); var missing = Live(secondId);
            present.RetryOriginals["responses/old.json"] = [3]; present.PdfPath = "still-old.pdf";
            Reset(present, missing); SelectQuietly(present);
            var preflightChecks = RejectWithoutChanges([fresh[0]], accountA, present, missing);
            var duplicate = new BoardItem { Id = firstId, Title = "A 的独立本地来源", Account = accountA, Document = Document(firstId, accountA) };
            Reset(present, duplicate); SelectQuietly(present);
            preflightChecks &= RejectWithoutChanges([fresh[0]], accountA, present, duplicate);
            Require(preflightChecks,
                "第二条缺失或升级后同账号同 ID 重复时完整预检失败，全部行、文档、缓存和选择保持原样");
            return checks;
        }
        finally
        {
            failed.Clear(); failed.AddRange(previousFailed);
            var wasSelecting = selecting; selecting = true;
            try
            {
                boards.Clear(); foreach (var item in previousBoards) boards.Add(item);
                foreach (var item in previousSelected) if (boards.Contains(item)) BoardList.SelectedItems.Add(item);
            }
            finally { selecting = wasSelecting; }
            current = previousCurrent; currentItem = previousItem; currentPdf = previousPdf; currentImage = previousImage; offlineVisualPath = previousVisual;
            ClearMediaViews(); BoardTitle.Text = previousTitle; BoardMetadataText.Text = previousMetadata; DiagnosticsText.Text = previousDiagnostics;
            UpdatePreviewActions();
            if (previousCurrent is not null) await ShowDocumentAsync(previousCurrent);
        }
    }
}
