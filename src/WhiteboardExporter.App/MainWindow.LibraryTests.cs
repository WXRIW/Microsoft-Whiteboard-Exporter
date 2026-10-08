using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using WhiteboardExporter.Core;

namespace WhiteboardExporter.App;

public partial class MainWindow
{
    internal bool LibraryDefaultsChecked() => FormatHtml.IsChecked == true && TransparentBox.IsChecked == true;

    internal async Task<IReadOnlyList<string>> TestLibraryAsync(BoardDocument template, string directory)
    {
        var checks = new List<string>();
        void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        var root = Path.Combine(directory, "library");
        Directory.CreateDirectory(root);
        var firstAccount = ExportAccount.FromLabel("目录验收账号 A");
        var secondAccount = ExportAccount.FromLabel("目录验收账号 B");
        var firstAccountDirectory = await AccountDirectoryService.ResolveAsync(root, firstAccount);
        var secondAccountDirectory = await AccountDirectoryService.ResolveAsync(root, secondAccount);
        const string sharedId = "00000000-0000-0000-0000-000000000101";
        var fullDirectory = Path.Combine(firstAccountDirectory, "00 完整白板__" + sharedId);
        var secondDirectory = Path.Combine(secondAccountDirectory, "01 同 ID 白板__" + sharedId);
        Directory.CreateDirectory(fullDirectory); Directory.CreateDirectory(secondDirectory);
        BoardDocument Clone(string title, ExportAccount account)
        {
            var copy = new BoardDocument { Scene = (JsonObject)template.Scene.DeepClone() };
            copy.Scene["board"]!["id"] = sharedId; copy.Scene["board"]!["title"] = title;
            copy.Scene["board"]!["account"] = account.ToJson();
            foreach (var pair in template.Assets) copy.Assets[pair.Key] = pair.Value;
            foreach (var pair in template.Originals) copy.Originals[pair.Key] = pair.Value;
            return copy;
        }
        await ArchiveService.SaveJsonAsync(Clone("00 完整白板", firstAccount), Path.Combine(fullDirectory, "scene.json"));
        await File.WriteAllTextAsync(Path.Combine(fullDirectory, "board.wbarchive"), "deliberately damaged archive");
        await ArchiveService.SaveAsync(Clone("01 同 ID 白板", secondAccount), Path.Combine(secondDirectory, "board.wbarchive"));
        var originalExports = Path.GetDirectoryName(directory)!;
        foreach (var extension in new[] { "pdf", "png", "jpg", "html", "svg" })
            File.Copy(Path.Combine(originalExports, "markers." + extension), Path.Combine(fullDirectory, "board." + extension), true);
        var mediaDirectories = new Dictionary<string, string>();
        var mediaIndex = 102;
        foreach (var extension in new[] { "html", "svg", "pdf", "png" })
        {
            var folder = Path.Combine(firstAccountDirectory, "媒体 " + extension + "__00000000-0000-0000-0000-" + (mediaIndex++).ToString("D12"));
            Directory.CreateDirectory(folder);
            File.Copy(Path.Combine(originalExports, "markers." + extension), Path.Combine(folder, "board." + extension), true);
            mediaDirectories[extension] = folder;
        }
        async Task<Dictionary<string, (string Hash, DateTime Modified)>> SnapshotAsync()
        {
            var snapshot = new Dictionary<string, (string, DateTime)>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                snapshot[path] = (await HashFileAsync(path, CancellationToken.None), File.GetLastWriteTimeUtc(path));
            return snapshot;
        }
        var before = await SnapshotAsync();
        var previousBoards = boards.ToArray(); var previousCurrent = current; var previousSelection = BoardList.SelectedItem as BoardItem;
        try
        {
            var scan = await OpenLibraryAsync(root);
            var full = boards.Single(item => item.LibraryEntry?.DirectoryPath == fullDirectory);
            var second = boards.Single(item => item.LibraryEntry?.DirectoryPath == secondDirectory);
            Require(scan.Entries.Count == 6 && boards.Count == 6 && full.LibraryEntry!.SourceCandidates.Count == 3 &&
                full.LibraryEntry.ImagePaths.Count == 2 && full.PdfPath is not null && full.HtmlPath is not null && full.SvgPath is not null,
                "目录扫描将备份、JSON、PDF、PNG、JPEG、SVG 和 HTML 合并为一张白板");
            Require(full.Id == sharedId && second.Id == sharedId && full.Account?.Key == firstAccount.Key &&
                second.Account?.Key == secondAccount.Key && full.LibraryEntry!.Key != second.LibraryEntry!.Key,
                "不同账号中的相同白板 ID 保留独立条目与所属账号");
            Require(boards.Count(item => item.Document is not null) == 1 && full.Document is not null && second.Document is null,
                "目录打开只加载首张白板，其余原始数据按选择延迟导入");
            Require(full.Document!.Id == sharedId && full.Document.ElementCount == template.ElementCount &&
                full.LibraryWarning is not null && ImageFileBox.Items.Count == 2 && OriginalPreviewButton.IsEnabled &&
                HtmlPreviewButton.IsEnabled && SvgPreviewButton.IsEnabled,
                "损坏备份自动回退 JSON，并关联图片格式与可用预览按钮");
            var retained = full.Document!;
            await ShowOfflineVisualAsync(full, full.HtmlPath!);
            var htmlScene = await sceneHost.EvaluateAsync("window.wb.getScene()");
            var htmlText = (await sceneHost.EvaluateAsync("document.querySelector('#stage').textContent"))?.GetValue<string>();
            await ShowOfflineVisualAsync(full, full.SvgPath!);
            var svgScene = await sceneHost.EvaluateAsync("window.wb.getScene()");
            await ShowDocumentAsync(retained);
            Require(htmlScene is null && svgScene is null && htmlText?.Contains("中文") == true &&
                ReferenceEquals(current, retained) && (await sceneHost.EvaluateAsync("window.wb.getScene().board.id"))?.GetValue<string>() == sharedId,
                "保存的 HTML／SVG 保留中文并只作媒体预览，返回原始重绘仍使用原 JSON");
            var fullImagePath = full.ImagePath; var secondImagePath = second.ImagePath; var previousImage = currentImage;
            try
            {
                async Task<string> GenerateMissingImageAsync(BoardItem item)
                {
                    await LoadLibraryDocumentAsync(item, CancellationToken.None);
                    await ShowDocumentAsync(item.Document!);
                    await ShowOfflineVisualAsync(item, full.HtmlPath!);
                    item.ImagePath = null; currentImage = null; ImagePreview.Source = null;
                    PreviewTabs.SelectedItem = ImageTab;
                    var deadline = Stopwatch.StartNew();
                    while (deadline.Elapsed < TimeSpan.FromSeconds(15) &&
                        (previewUpdating || currentImage is null || ImagePreview.Source is null || !File.Exists(currentImage)))
                        await Task.Delay(30);
                    if (previewUpdating || currentImage is null || ImagePreview.Source is not BitmapSource bitmap ||
                        bitmap.PixelWidth != 2048 || bitmap.PixelHeight != 1536 || !File.Exists(currentImage))
                        throw new InvalidOperationException("HTML 预览后无法从原始 JSON 生成 2048 × 1536 图片：" + StatusText.Text);
                    return currentImage;
                }
                var firstCache = await GenerateMissingImageAsync(full);
                var secondCache = await GenerateMissingImageAsync(second);
                Require(firstCache != secondCache && Path.GetFileName(firstCache).StartsWith(firstAccount.Key[..16] + "__", StringComparison.Ordinal) &&
                    Path.GetFileName(secondCache).StartsWith(secondAccount.Key[..16] + "__", StringComparison.Ordinal) &&
                    Path.GetFileName(firstCache).EndsWith(sharedId + ".png", StringComparison.Ordinal) &&
                    Path.GetFileName(secondCache).EndsWith(sharedId + ".png", StringComparison.Ordinal),
                    "HTML 媒体预览后缺失图片从原始 JSON 生成 2048 × 1536 图像，相同 ID 按账号隔离预览缓存");
            }
            finally
            {
                full.ImagePath = fullImagePath; second.ImagePath = secondImagePath; currentImage = previousImage;
                await ShowDocumentAsync(retained);
            }
            foreach (var extension in new[] { "html", "svg" })
            {
                var media = boards.Single(item => item.LibraryEntry?.DirectoryPath == mediaDirectories[extension]);
                SelectQuietly(media); await PreviewBoardAsync(media);
                Require(current is null && media.Document is null && !OriginalPreviewButton.IsEnabled &&
                    await sceneHost.EvaluateAsync("window.wb.getScene()") is null &&
                    (await sceneHost.EvaluateAsync("document.querySelector('#stage>svg').getAttribute('viewBox')"))?.GetValue<string>() == "-100 -100 2400 1800" &&
                    (await sceneHost.EvaluateAsync("document.querySelectorAll('#stage foreignObject').length"))?.GetValue<int>() > 0,
                    "仅有 " + extension.ToUpperInvariant() + " 的目录条目可离线预览，保留负坐标和富文本且不伪造原始数据");
                using var shot = File.Create(Path.Combine(directory, extension + "-media-preview.png"));
                await SceneView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, shot);
            }
            var pdf = boards.Single(item => item.LibraryEntry?.DirectoryPath == mediaDirectories["pdf"]);
            SelectQuietly(pdf); await PreviewBoardAsync(pdf);
            Require(current is null && pdf.Document is null && PreviewTabs.SelectedItem == PdfTab && currentPdf == pdf.PdfPath &&
                new Uri(PdfView.CoreWebView2.Source).LocalPath.Equals(pdf.PdfPath, StringComparison.OrdinalIgnoreCase),
                "仅有 PDF 的目录条目直接打开应用内 PDF 预览");
            var image = boards.Single(item => item.LibraryEntry?.DirectoryPath == mediaDirectories["png"]);
            SelectQuietly(image); await PreviewBoardAsync(image);
            Require(current is null && image.Document is null && PreviewTabs.SelectedItem == ImageTab &&
                ImagePreview.Source is BitmapSource bitmap && bitmap.PixelWidth == 2400 && bitmap.PixelHeight == 1800 &&
                currentImage == image.ImagePath && ImageScale.ScaleX > 0 && ImageScale.ScaleX < 1,
                "仅有图片的目录条目直接打开原尺寸图片并适应窗口");
            var previousOutput = OutputBox.Text;
            var formatBoxes = new[] { FormatArchive, FormatJson, FormatPdf, FormatPng, FormatJpeg, FormatSvg, FormatHtml };
            var previousFormats = formatBoxes.Select(box => box.IsChecked).ToArray();
            var batchRoot = Path.Combine(directory, "multi-account-export");
            try
            {
                await LoadLibraryDocumentAsync(second, CancellationToken.None);
                SelectQuietly(full);
                selecting = true;
                try { BoardList.SelectedItems.Add(second); }
                finally { selecting = false; }
                OutputBox.Text = batchRoot;
                foreach (var box in formatBoxes) box.IsChecked = false;
                FormatArchive.IsChecked = FormatJson.IsChecked = true;
                await ExportBatchAsync([full, second], CancellationToken.None);
                var accountFolders = Directory.GetDirectories(batchRoot);
                var keys = new HashSet<string>();
                var exportedData = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var valid = accountFolders.Length == 2;
                foreach (var folder in accountFolders)
                {
                    var account = await AccountDirectoryService.ReadAccountAsync(folder);
                    if (account is null || account.Key != firstAccount.Key && account.Key != secondAccount.Key) { valid = false; continue; }
                    keys.Add(account.Key);
                    var state = await ReadStateAsync(Path.Combine(folder, "export-state.json"), CancellationToken.None);
                    var entry = state[EntryKey(sharedId)];
                    var files = (entry?["files"] as JsonArray)?.Select(file => file!.GetValue<string>()).Where(path =>
                        Path.GetExtension(path) is ".wbarchive" or ".json").ToArray() ?? [];
                    valid &= entry?["status"]?.GetValue<string>() == "complete" && entry?["accountKey"]?.GetValue<string>() == account.Key && files.Length == 2;
                    foreach (var path in files)
                    {
                        var restored = await importer.ImportAsync(path);
                        valid &= exportedData.Add(path) && restored.Id == sharedId && ReadDocumentAccount(restored)?.Key == account.Key &&
                            Path.GetFileName(Path.GetDirectoryName(path)!).EndsWith("__" + sharedId, StringComparison.OrdinalIgnoreCase) &&
                            Path.GetDirectoryName(Path.GetDirectoryName(path)!)!.Equals(folder, StringComparison.OrdinalIgnoreCase);
                    }
                }
                Require(valid && keys.Count == 2 && exportedData.Count == 4 && full.ExportError is null && second.ExportError is null,
                    "真实批量导出将不同账号的相同 ID 分存独立账号目录、任务记录、备份和 JSON");
            }
            finally
            {
                OutputBox.Text = previousOutput;
                for (var index = 0; index < formatBoxes.Length; index++) formatBoxes[index].IsChecked = previousFormats[index];
            }
            var repeated = await OpenLibraryAsync(root);
            var after = await SnapshotAsync();
            Require(repeated.Entries.Count == scan.Entries.Count && boards.Select(item => item.LibraryEntry!.Key).Distinct().Count() == 6 &&
                before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var value) && value == pair.Value),
                "重复打开目录不产生重复条目，预览不改变文件字节和修改时间");
            return checks;
        }
        finally
        {
            selecting = true;
            try { boards.Clear(); foreach (var item in previousBoards) boards.Add(item); BoardList.SelectedItem = previousSelection; }
            finally { selecting = false; }
            if (previousCurrent is not null) await ShowDocumentAsync(previousCurrent);
            else { current = null; currentItem = previousSelection; currentPdf = currentImage = offlineVisualPath = null; ClearMediaViews(); UpdatePreviewActions(); }
        }
    }
}
