using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Win32;
using WhiteboardExporter.App.Cloud;
using WhiteboardExporter.App.Services;
using WhiteboardExporter.Core;

namespace WhiteboardExporter.App;

public partial class MainWindow
{
    private ExportAccount? activeCloudAccount;
    private CancellationTokenSource? libraryPreviewCancellation;
    private string? offlineVisualPath;

    private async void OpenDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "打开已导出目录（可选择总目录、账号目录或单张白板目录）" };
        if (Directory.Exists(OutputBox.Text)) dialog.InitialDirectory = Path.GetFullPath(OutputBox.Text);
        if (dialog.ShowDialog(this) == true) await RunAsync(ct => OpenLibraryAsync(dialog.FolderName, ct));
    }

    internal async Task<ExportLibraryScanResult> OpenLibraryAsync(string directory, CancellationToken ct = default)
    {
        libraryPreviewCancellation?.Cancel();
        var scan = await ExportLibraryService.ScanAsync(directory, ct);
        ct.ThrowIfCancellationRequested();
        current = null; currentItem = null; currentPdf = currentImage = offlineVisualPath = null;
        ClearMediaViews();
        boards.Clear(); failed.Clear(); SearchBox.Text = "";
        foreach (var entry in scan.Entries)
            boards.Add(new BoardItem
            {
                Id = entry.BoardIdHint ?? entry.Key, Title = entry.TitleFallback,
                Account = entry.Account, LibraryEntry = entry, PdfPath = entry.PdfPath,
                ImagePath = entry.ImagePaths.FirstOrDefault(), HtmlPath = entry.HtmlPath, SvgPath = entry.SvgPath,
                Subtitle = LibraryFormats(entry)
            });
        LibraryLocationText.Text = "本地目录：" + scan.RootDirectory;
        LibraryLocationText.ToolTip = scan.RootDirectory;
        BoardTitle.Text = "选择一张白板"; BoardMetadataText.Text = "";
        DiagnosticsText.Text = scan.Entries.Count == 0 ? "目录中没有可预览的导出内容。" : "选择列表中的白板，直接查看原始重绘、PDF、图片或离线 HTML。";
        UpdatePreviewActions();
        if (boards.FirstOrDefault() is { } first)
        {
            SelectQuietly(first);
            try { await PreviewBoardAsync(first, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
            {
                first.LibraryWarning = "此条目的预览未能打开：" + exception.Message;
                DiagnosticsText.Text = first.LibraryWarning + "\n仍可选择目录中的其他白板。";
            }
        }
        StatusText.Text = $"已打开目录，找到 {scan.Entries.Count} 张白板。" +
            (scan.Issues.Count > 0 ? $" {scan.Issues.Count} 处文件或目录未能读取。" : "");
        return scan;
    }

    private static string LibraryFormats(ExportLibraryEntry entry)
    {
        var formats = new List<string>();
        if (entry.SourceCandidates.Any(path => Path.GetExtension(path).Equals(".wbarchive", StringComparison.OrdinalIgnoreCase))) formats.Add("备份");
        if (entry.SourceCandidates.Any(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))) formats.Add("JSON");
        if (entry.PdfPath is not null) formats.Add("PDF");
        if (entry.ImagePaths.Count > 0) formats.Add("图片");
        if (entry.HtmlPath is not null) formats.Add("HTML");
        if (entry.SvgPath is not null) formats.Add("SVG");
        return "本地导出 · " + string.Join(" / ", formats);
    }

    private async Task LoadLibraryDocumentAsync(BoardItem item, CancellationToken ct)
    {
        if (item.Document is not null || item.LibraryEntry is not { } entry) return;
        var failures = new List<string>();
        foreach (var source in entry.SourceCandidates.Where(path => Path.GetExtension(path).ToLowerInvariant() is ".wbarchive" or ".json" or ".zip"))
        {
            try
            {
                var document = await importer.ImportAsync(source, ct);
                ct.ThrowIfCancellationRequested();
                item.Document = document; item.Id = document.Id; item.Title = document.Title;
                var savedAccount = ReadDocumentAccount(document);
                var accountConflict = item.Account is not null && savedAccount is not null && item.Account.Key != savedAccount.Key;
                item.Account = savedAccount ?? item.Account ?? AccountFromLibraryDirectory(entry);
                ResourceRecoveryService.ValidateLocalAssets(document);
                item.Subtitle = StatusLabel(document) + " · 本地导出";
                item.LibraryWarning = accountConflict ? "目录账号与原始备份的账号不一致，已保留备份中记录的所属账号。" :
                    failures.Count > 0 ? "部分原始文件无法读取，已使用可用备份或 JSON。" : null;
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or UnauthorizedAccessException)
            { failures.Add(Path.GetFileName(source)); }
        }
        item.LibraryWarning = failures.Count > 0 ? "原始数据无法读取，仍可查看已保存的 PDF、图片或网页。" : null;
    }

    private async Task PreviewBoardAsync(BoardItem item, CancellationToken ct = default)
    {
        await LoadLibraryDocumentAsync(item, ct);
        ct.ThrowIfCancellationRequested();
        if (item.Document is not null) { await ShowDocumentAsync(item.Document, ct); return; }
        current = null; currentItem = item; currentPdf = File.Exists(item.PdfPath) ? item.PdfPath : null;
        currentImage = File.Exists(item.ImagePath) ? item.ImagePath : null; offlineVisualPath = null;
        ClearMediaViews();
        BoardTitle.Text = item.Title;
        BoardMetadataText.Text = item.LibraryEntry is { } entry ? "本地目录：" + entry.DirectoryPath : "本地文件：" + item.MediaPath;
        DiagnosticsText.Text = (item.LibraryWarning is { } warning ? warning + "\n" : "") + "当前为已保存的预览文件。原始备份或 JSON 可用于重新绘制和多格式导出。";
        UpdatePreviewActions();
        if (currentPdf is { } pdf) await ShowPdfAsync(pdf);
        else if (currentImage is { } image) ShowImage(image);
        else if (item.HtmlPath is { } html) await ShowOfflineVisualAsync(item, html, ct);
        else if (item.SvgPath is { } svg) await ShowOfflineVisualAsync(item, svg, ct);
        else if (item.MediaPath is { } media)
        { if (Path.GetExtension(media).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) await ShowPdfAsync(media); else ShowImage(media); }
        else if (item.Cloud is not null)
        { BoardMetadataText.Text = BoardDetails(item); DiagnosticsText.Text = "此白板尚未下载。点击“导出选中白板”获取原始包和离线画布。"; }
        else StatusText.Text = item.LibraryWarning ?? "该条目没有可预览的文件。";
    }

    private void UpdatePreviewActions()
    {
        OriginalPreviewButton.IsEnabled = currentItem?.Document is not null;
        HtmlPreviewButton.IsEnabled = File.Exists(currentItem?.HtmlPath);
        SvgPreviewButton.IsEnabled = File.Exists(currentItem?.SvgPath);
        previewUpdateDepth++;
        try
        {
            var paths = currentItem?.LibraryEntry?.ImagePaths ??
                (currentItem?.ImagePath is { } image ? (IReadOnlyList<string>)[image] :
                 currentItem?.MediaPath is { } media && Path.GetExtension(media).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" ? [media] : []);
            ImageFileBox.ItemsSource = paths.Select(path => new ImageFileChoice(path)).ToArray();
            ImageFileBox.SelectedItem = ImageFileBox.Items.Cast<ImageFileChoice>().FirstOrDefault(choice => choice.Path == currentImage) ?? ImageFileBox.Items.Cast<ImageFileChoice>().FirstOrDefault();
        }
        finally { previewUpdateDepth--; }
    }

    private async Task ShowOfflineVisualAsync(BoardItem item, string path, CancellationToken ct = default)
    {
        previewUpdateDepth++;
        try
        {
            currentItem = item; offlineVisualPath = path;
            SceneView.Visibility = Visibility.Visible; SceneTab.Header = Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase) ? "SVG 预览" : "离线 HTML";
            PreviewTabs.SelectedItem = SceneTab; PreviewTabs.UpdateLayout();
            var result = await OfflineMediaPreviewService.LoadAsync(sceneHost, path, ct);
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(currentItem, item) || offlineVisualPath != path) return;
            BoardTitle.Text = item.Title; BoardMetadataText.Text = "本地文件：" + path;
            DiagnosticsText.Text = $"离线预览 · {result.Width:0.##} × {result.Height:0.##}" +
                (result.BlockedResources > 0 ? $"\n{result.BlockedResources} 个外部资源已阻止。" : "") +
                (result.RemovedActiveContent > 0 ? "\n导入内容中的活动脚本或不安全标记已移除。" : "");
            UpdatePreviewActions();
        }
        finally { previewUpdateDepth--; }
    }

    private async void OriginalPreview_Click(object sender, RoutedEventArgs e)
    { if (currentItem?.Document is { } document) await RunAsync(ct => ShowDocumentAsync(document, ct)); }
    private async void HtmlPreview_Click(object sender, RoutedEventArgs e)
    { if (currentItem is { HtmlPath: { } path } item) await RunAsync(ct => ShowOfflineVisualAsync(item, path, ct)); }
    private async void SvgPreview_Click(object sender, RoutedEventArgs e)
    { if (currentItem is { SvgPath: { } path } item) await RunAsync(ct => ShowOfflineVisualAsync(item, path, ct)); }
    private void ImageFile_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!initialized || previewUpdating || ImageFileBox.SelectedItem is not ImageFileChoice choice) return;
        try { ShowImage(choice.Path); }
        catch (Exception exception) { StatusText.Text = "图片预览失败：" + exception.Message; }
    }
    private sealed record ImageFileChoice(string Path) { public string Name => System.IO.Path.GetFileName(Path); }

    private static ExportAccount? ReadDocumentAccount(BoardDocument document)
    {
        try { return ExportAccount.FromJson(document.Scene["board"]?["account"]); }
        catch (InvalidDataException) { return null; }
    }
    private static ExportAccount BoardAccount(BoardItem item) => item.Account ?? (item.Document is { } document ? ReadDocumentAccount(document) : null) ?? ExportAccount.Local;
    private static ExportAccount? AccountFromLibraryDirectory(ExportLibraryEntry entry)
    {
        if (entry.AccountDirectory is not { } directory) return null;
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).ToUpperInvariant();
        var key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("WhiteboardExporter.LibraryAccount/v1/" + canonical)));
        var name = entry.AccountLabel ?? Path.GetFileName(directory);
        return new ExportAccount(key, name.Length > 200 ? name[..200] : name, Kind: "local", Source: "local-directory").Validate();
    }
    private static bool SameAccount(BoardItem item, ExportAccount account) => BoardAccount(item).Key == account.Key;
    private static ExportAccount ToExportAccount(CloudAccount account) => new ExportAccount(account.Key, account.DisplayName, account.Email, account.Kind, account.Source).Validate();

    private async Task<ExportAccount?> ReadCurrentAccountAsync(CancellationToken ct, bool resetManual = false)
    {
        var identity = await cloud!.GetAccountAsync(ct);
        if (identity is null && (activeCloudAccount is not null || resetManual)) AccountDirectoryBox.Text = "";
        activeCloudAccount = identity is null ? null : ToExportAccount(identity);
        AccountInfoText.Text = activeCloudAccount is { } account ? "当前账号：" + (account.Email ?? account.DisplayName) : "账号尚未识别；可填写独立账号目录名称。";
        AccountDirectoryBox.IsEnabled = activeCloudAccount is null;
        if (activeCloudAccount is not null) AccountDirectoryBox.Text = activeCloudAccount.Email ?? activeCloudAccount.DisplayName;
        return activeCloudAccount;
    }
    private ExportAccount? CurrentAccountOrManual()
    {
        if (activeCloudAccount is not null) return activeCloudAccount;
        return string.IsNullOrWhiteSpace(AccountDirectoryBox.Text) ? null : ExportAccount.FromLabel(AccountDirectoryBox.Text);
    }

    private static ExportAccount? CloudBoardAccount(BoardItem item) => item.Account ??
        (item.Document is { } document ? ReadDocumentAccount(document) : null);
    private static bool IsMicrosoftAccount(ExportAccount account) => account.Kind is "msa" or "aad";
    private static bool CanBindCloudAccount(BoardItem item, ExportAccount account)
    {
        var previous = CloudBoardAccount(item);
        return previous is null || previous.Key == account.Key || previous.Kind == "manual" && IsMicrosoftAccount(account);
    }
    private static bool NeedsCloudAccountBindingForRetry(IEnumerable<BoardItem> items) =>
        items.Any(item => item.Cloud is not null && CloudBoardAccount(item) is null);

    // Validate the complete selection before changing any live row or its caches.
    // A manually named directory is not an authenticated Microsoft identity.
    private IReadOnlyList<BoardItem> BindFreshCloudBoards(IReadOnlyList<CloudBoard> fresh, ExportAccount account, IReadOnlyList<BoardItem> selected)
    {
        var cloudSelections = selected.Where(item => item.Cloud is not null).ToHashSet();
        foreach (var item in cloudSelections)
        {
            if (!CanBindCloudAccount(item, account))
                throw new InvalidOperationException("当前登录账号与所选白板所属账号不同。请重新读取当前账号的列表，或打开对应账号的本地备份。");
            if (!fresh.Any(board => board.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("所选白板不在当前账号的最新列表中，请重新读取列表。");
        }
        if (selected.GroupBy(item => (cloudSelections.Contains(item) ? account.Key : BoardAccount(item).Key) + "\0" + item.Id.ToUpperInvariant()).Any(group => group.Count() > 1))
            throw new InvalidOperationException("所选条目中同一账号有重复的白板 ID。请分别选择对应来源，避免覆盖其他白板。");

        var focused = BoardList.SelectedItem as BoardItem;
        ApplyCloudBoards(fresh, account);
        var rebound = selected.Select(item => cloudSelections.Contains(item)
            ? boards.First(live => live.Cloud is not null && SameAccount(live, account) && live.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase))
            : item).ToArray();
        var focusIndex = focused is null ? -1 : Enumerable.Range(0, selected.Count).FirstOrDefault(index => ReferenceEquals(selected[index], focused), -1);
        var newFocus = focusIndex >= 0 ? rebound[focusIndex] : rebound.FirstOrDefault();
        var wasSelecting = selecting;
        selecting = true;
        try
        {
            BoardList.SelectedItems.Clear();
            if (newFocus is not null && boards.Contains(newFocus)) BoardList.SelectedItem = newFocus;
            foreach (var item in rebound)
                if (boards.Contains(item) && !BoardList.SelectedItems.Contains(item)) BoardList.SelectedItems.Add(item);
        }
        finally { selecting = wasSelecting; }
        if (newFocus is { Cloud: not null, Document: null })
        {
            current = null; currentItem = newFocus; currentPdf = currentImage = offlineVisualPath = null;
            ClearMediaViews(); SceneTab.Header = "原始重绘";
            BoardTitle.Text = newFocus.Title; BoardMetadataText.Text = BoardDetails(newFocus);
            DiagnosticsText.Text = "此白板尚未下载。正在从当前账号获取原始包和离线画布。";
            UpdatePreviewActions();
        }
        return rebound;
    }

    private async Task ExportBatchAsync(IReadOnlyList<BoardItem> selected, CancellationToken ct, bool retrySources = false)
    {
        if (!SelectedFormats().Any()) throw new InvalidOperationException("请至少选择一种文件格式。");
        var root = Path.GetFullPath(OutputBox.Text);
        foreach (var item in selected) await LoadLibraryDocumentAsync(item, ct);
        if (selected.Any(item => item.Cloud is not null))
        {
            await EnsureCloudAsync(); PreviewTabs.SelectedItem = LoginTab;
            var fresh = await cloud!.RefreshBoardsAsync(ct);
            await ReadCurrentAccountAsync(ct);
            var account = CurrentAccountOrManual() ?? throw new InvalidOperationException("无法识别当前登录账号。请在账号目录名称中填写该账号的独立名称后再次导出。");
            selected = BindFreshCloudBoards(fresh, account, selected);
        }
        if (selected.GroupBy(item => BoardAccount(item).Key + "\0" + item.Id.ToUpperInvariant()).Any(group => group.Count() > 1))
            throw new InvalidOperationException("所选条目中同一账号有重复的白板 ID。请分别选择对应来源，避免覆盖其他白板。");
        failed.Clear();
        foreach (var item in selected.Where(item => item.Document is null && item.Cloud is null))
        {
            item.ExportError = item.LibraryWarning ?? "原始备份或 JSON 无法读取，只能查看已有预览文件。";
            failed.Add(item);
        }
        foreach (var group in selected.Where(item => item.Document is not null || item.Cloud is not null).GroupBy(item => BoardAccount(item).Key))
        {
            ct.ThrowIfCancellationRequested();
            var account = BoardAccount(group.First());
            var output = await AccountDirectoryService.ResolveAsync(root, account, ct);
            foreach (var item in group)
            {
                item.Account = account;
                if (item.Document is { } document) document.Scene["board"]!["account"] = account.ToJson();
            }
            await ExportAccountBatchAsync(group.ToArray(), output, ct, retrySources);
        }
        var succeeded = selected.Count(item => item.ExportError is null);
        var warnings = selected.Count(item => item.ExportError is null && item.Document?.Status != "complete");
        StatusText.Text = $"导出完成：{succeeded} 张成功，{failed.Count} 张失败，{warnings} 张含内容警告。按账号保存于：{root}";
        var empty = selected.Count(item => item.ExportError is null && item.Document is { ElementCount: 0 } document &&
            (document.Scene["diagnostics"]?["items"] as JsonArray)?.Any(d => d?["code"]?.GetValue<string>() == "EMPTY_CANVAS") == true);
        if (empty > 0) StatusText.Text += $"\n其中 {empty} 张微软源导出为空画布；若在线有内容，请等待加载完成后重新获取。";
    }
}
