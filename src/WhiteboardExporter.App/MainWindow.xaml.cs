using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using WhiteboardExporter.App.Cloud;
using WhiteboardExporter.App.Services;
using WhiteboardExporter.Core;

namespace WhiteboardExporter.App;

public partial class MainWindow : Window
{
    private readonly string[] arguments;
    private readonly ObservableCollection<BoardItem> boards = [];
    private readonly List<BoardItem> failed = [];
    private LocalWebHost sceneHost = null!, exportHost = null!;
    private DesktopExportService exporter = null!;
    private ImportService importer = null!;
    private ResourceRecoveryService resourceRecovery = null!;
    private CloudWhiteboardService? cloud;
    private BoardDocument? current;
    private CancellationTokenSource? operation;
    private bool initialized, busy, pdfInitialized, selecting;
    private int previewUpdateDepth, pdfPreviewGeneration;
    private bool previewUpdating => previewUpdateDepth > 0;
    private string? currentPdf, currentImage;
    private BoardItem? currentItem;
    private readonly string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhiteboardExporter");

    public MainWindow(string[] arguments)
    {
        this.arguments = arguments;
        var smokeIndex = Array.IndexOf(arguments, "--smoke-test");
        var verifyIndex = Array.IndexOf(arguments, "--verify-import");
        var cloudIndex = Array.IndexOf(arguments, "--verify-cloud");
        var cloudMetadataIndex = Array.IndexOf(arguments, "--verify-cloud-metadata");
        var libraryIndex = Array.IndexOf(arguments, "--verify-library");
        if (smokeIndex >= 0 || verifyIndex >= 0 || cloudIndex >= 0 || cloudMetadataIndex >= 0 || libraryIndex >= 0)
        {
            var outputIndex = libraryIndex >= 0 ? libraryIndex + 2 : cloudMetadataIndex >= 0 ? cloudMetadataIndex + 1 : cloudIndex >= 0 ? cloudIndex + 2 : verifyIndex >= 0 ? verifyIndex + 2 : smokeIndex + 1;
            var smokeOutput = outputIndex < arguments.Length ? Path.GetFullPath(arguments[outputIndex]) : Path.Combine(Environment.CurrentDirectory, "artifacts", "smoke");
            dataDirectory = Path.Combine(smokeOutput, "profiles");
        }
        InitializeComponent();
        OutputBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Whiteboard Exports");
        BoardList.ItemsSource = boards;
        CollectionViewSource.GetDefaultView(boards).Filter = item => item is BoardItem board &&
            (string.IsNullOrWhiteSpace(SearchBox.Text) || board.Title.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase) || board.Id.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var smoke = Array.IndexOf(arguments, "--smoke-test");
        var verify = Array.IndexOf(arguments, "--verify-import");
        var cloudVerify = Array.IndexOf(arguments, "--verify-cloud");
        var cloudMetadataVerify = Array.IndexOf(arguments, "--verify-cloud-metadata");
        var libraryVerify = Array.IndexOf(arguments, "--verify-library");
        if (smoke >= 0 || verify >= 0 || cloudVerify >= 0 || cloudMetadataVerify >= 0 || libraryVerify >= 0) { Left = -15000; Top = -15000; ShowInTaskbar = false; }
        // Reject a report/profile destination inside the source before initializing
        // WebView2: even its profile setup must not write into the read-only tree.
        if (libraryVerify >= 0 && libraryVerify + 2 < arguments.Length)
        {
            try { RequireSeparateLibraryReport(arguments[libraryVerify + 1], arguments[libraryVerify + 2]); }
            catch { Application.Current.Shutdown(1); return; }
        }
        try
        {
            sceneHost = new LocalWebHost(SceneView, Path.Combine(dataDirectory, "Browser", "Offline"));
            exportHost = new LocalWebHost(ExportView, Path.Combine(dataDirectory, "Browser", "Export"), softwareRendering: true);
            await sceneHost.InitializeAsync();
            await exportHost.InitializeAsync();
            exporter = new DesktopExportService(exportHost);
            importer = new ImportService(exportHost);
            resourceRecovery = new ResourceRecoveryService(Path.Combine(dataDirectory, "Resources"));
            resourceRecovery.StatusChanged += message => StatusText.Text = message;
            initialized = true;
            StatusText.Text = "已就绪。导入本地文件，或登录微软白板后读取列表。";
            if (libraryVerify >= 0)
            {
                if (libraryVerify + 2 >= arguments.Length) throw new ArgumentException("用法：--verify-library 已导出目录 验证输出目录（只读预览）");
                var success = await VerifyLibraryAsync(Path.GetFullPath(arguments[libraryVerify + 1]), Path.GetFullPath(arguments[libraryVerify + 2]));
                Application.Current.Shutdown(success ? 0 : 1);
                return;
            }
            if (cloudMetadataVerify >= 0)
            {
                if (cloudMetadataVerify + 1 >= arguments.Length) throw new ArgumentException("用法：--verify-cloud-metadata 输出目录（仅读取已登录账号的白板列表编辑时间）");
                var success = await CloudMetadataVerification.RunAsync(this, Path.GetFullPath(arguments[cloudMetadataVerify + 1]));
                Application.Current.Shutdown(success ? 0 : 1);
                return;
            }
            if (cloudVerify >= 0)
            {
                if (cloudVerify + 2 >= arguments.Length) throw new ArgumentException("用法：--verify-cloud 白板ID清单.json 输出目录（使用已登录的本机账号）");
                var success = await CloudVerification.RunAsync(this, Path.GetFullPath(arguments[cloudVerify + 1]), Path.GetFullPath(arguments[cloudVerify + 2]));
                Application.Current.Shutdown(success ? 0 : 1);
                return;
            }
            if (verify >= 0)
            {
                if (verify + 2 >= arguments.Length) throw new ArgumentException("用法：--verify-import 输入清单.json 输出目录");
                var success = await ImportVerification.RunAsync(this, Path.GetFullPath(arguments[verify + 1]), Path.GetFullPath(arguments[verify + 2]));
                Application.Current.Shutdown(success ? 0 : 1);
                return;
            }
            if (smoke >= 0)
            {
                var folder = smoke + 1 < arguments.Length ? Path.GetFullPath(arguments[smoke + 1]) : Path.Combine(Environment.CurrentDirectory, "artifacts", "smoke");
                var result = await SmokeTests.RunAsync(this, folder);
                Application.Current.Shutdown(result ? 0 : 1);
                return;
            }
            var path = arguments.FirstOrDefault(argument => File.Exists(argument) || Directory.Exists(argument));
            if (path is not null && Directory.Exists(path)) await OpenLibraryAsync(path);
            else if (path is not null) await ImportPathAsync(path);
            else await LoadSampleAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "初始化失败：" + ex.Message;
            if (cloudMetadataVerify >= 0)
            {
                var folder = cloudMetadataVerify + 1 < arguments.Length ? Path.GetFullPath(arguments[cloudMetadataVerify + 1]) :
                    Path.Combine(Environment.CurrentDirectory, "artifacts", "cloud-metadata");
                await CloudMetadataVerification.WriteFailureAsync(folder, ex);
                Application.Current.Shutdown(1);
                return;
            }
            if (libraryVerify >= 0)
            {
                var folder = libraryVerify + 2 < arguments.Length ? Path.GetFullPath(arguments[libraryVerify + 2]) : Path.Combine(Environment.CurrentDirectory, "artifacts", "library-verification");
                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(Path.Combine(folder, "library-result.json"), new JsonObject { ["success"] = false, ["errorType"] = ex.GetType().Name }.ToJsonString(LocalWebHost.JsonOptions));
                Application.Current.Shutdown(1);
                return;
            }
            if (smoke >= 0 || verify >= 0 || cloudVerify >= 0)
            {
                var outputIndex = cloudVerify >= 0 ? cloudVerify + 2 : verify >= 0 ? verify + 2 : smoke + 1;
                var folder = outputIndex < arguments.Length ? Path.GetFullPath(arguments[outputIndex]) : Path.Combine(Environment.CurrentDirectory, "artifacts", "smoke");
                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(Path.Combine(folder, "smoke-result.json"), JsonSerializer.Serialize(new { success = false, error = ex.ToString() }, LocalWebHost.JsonOptions));
                Application.Current.Shutdown(1);
            }
            else MessageBox.Show("本地预览初始化失败。请确认 WebView2 Runtime 已安装。\n\n" + ex.Message, "白板导出器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (!initialized || busy) return;
        busy = true; operation = new CancellationTokenSource();
        ImportButton.IsEnabled = OpenDirectoryButton.IsEnabled = LogoutButton.IsEnabled = SampleButton.IsEnabled = LoginButton.IsEnabled = RefreshButton.IsEnabled = ExportButton.IsEnabled = BoardList.IsEnabled = false;
        libraryPreviewCancellation?.Cancel();
        CancelButton.IsEnabled = true;
        try { await action(operation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "任务已取消。完成的备份和导出文件仍保留，可重新导出以继续。"; }
        catch (Exception ex) { StatusText.Text = "操作失败：" + ex.Message; MessageBox.Show(ex.Message, "白板导出器", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            operation.Dispose(); operation = null; busy = false;
            ImportButton.IsEnabled = OpenDirectoryButton.IsEnabled = LogoutButton.IsEnabled = SampleButton.IsEnabled = LoginButton.IsEnabled = RefreshButton.IsEnabled = ExportButton.IsEnabled = BoardList.IsEnabled = true;
            CancelButton.IsEnabled = false; TaskProgress.IsIndeterminate = false;
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入白板或打开预览", Multiselect = true, Filter = "白板与预览文件|*.wbarchive;*.zip;*.json;*.html;*.whiteboard;*.pdf;*.png;*.jpg;*.jpeg|全部文件|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync(async ct => { foreach (var path in dialog.FileNames) { ct.ThrowIfCancellationRequested(); await ImportPathAsync(path, ct); } });
    }
    internal async Task ImportPathAsync(string path, CancellationToken ct = default, bool isSample = false)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".pdf" or ".png" or ".jpg" or ".jpeg")
        {
            var mediaId = Path.GetFullPath(path);
            var item = boards.FirstOrDefault(board => board.MediaPath is not null && board.Id.Equals(mediaId, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            { item = new BoardItem { Id = mediaId, Title = Path.GetFileName(path), MediaPath = path, Subtitle = "本地预览文件" }; boards.Add(item); }
            current = null; currentItem = item; currentPdf = currentImage = null;
            ClearMediaViews();
            SelectQuietly(item);
            BoardTitle.Text = item.Title; BoardMetadataText.Text = "本地文件：" + path; DiagnosticsText.Text = "当前为预览文件。导入白板备份或 scene.json 后可离线重绘和多格式导出。";
            UpdatePreviewActions();
            if (extension == ".pdf") await ShowPdfAsync(path); else ShowImage(path);
            return;
        }
        StatusText.Text = "正在解析：" + Path.GetFileName(path);
        var document = await importer.ImportAsync(path, ct);
        await RecoverResourcesAsync(document, ct);
        var account = ReadDocumentAccount(document) ?? ExportAccount.Local;
        var existing = boards.FirstOrDefault(b => b.Id.Equals(document.Id, StringComparison.OrdinalIgnoreCase) && SameAccount(b, account));
        if (existing is null)
        {
            existing = new BoardItem { Id = document.Id, Title = document.Title, Document = document, Account = account, IsSample = isSample, Subtitle = StatusLabel(document) };
            boards.Add(existing);
        }
        else { existing.Document = document; existing.Title = document.Title; existing.IsSample = isSample; existing.PdfPath = existing.ImagePath = null; existing.Subtitle = StatusLabel(document); }
        SelectQuietly(existing);
        await ShowDocumentAsync(document, ct);
        StatusText.Text = $"已导入 {document.Title}，{document.ElementCount} 个对象。";
    }
    private Task LoadSampleAsync() => ImportPathAsync(Path.Combine(AppContext.BaseDirectory, "samples", "demo-scene.json"), isSample: true);
    private async void Sample_Click(object sender, RoutedEventArgs e) => await RunAsync(_ => LoadSampleAsync());

    private async Task EnsureCloudAsync()
    {
        if (cloud is not null) return;
        var loginData = arguments.Contains("--verify-cloud") || arguments.Contains("--verify-cloud-metadata")
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhiteboardExporter") : dataDirectory;
        cloud = new CloudWhiteboardService(CloudView, Path.Combine(loginData, "Browser", "Login"));
        cloud.StatusChanged += message => Dispatcher.Invoke(() => StatusText.Text = message);
        await cloud.InitializeAsync();
    }
    private async void Login_Click(object sender, RoutedEventArgs e) => await RunAsync(async _ =>
    {
        PreviewTabs.SelectedItem = LoginTab; await EnsureCloudAsync(); await cloud!.LoginAsync();
        StatusText.Text = "请在微软页面完成登录，然后点击“读取白板列表”。";
    });
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        PreviewTabs.SelectedItem = LoginTab; await EnsureCloudAsync();
        var list = await cloud!.RefreshBoardsAsync(ct);
        await ReadCurrentAccountAsync(ct, resetManual: true);
        ApplyCloudBoards(list, CurrentAccountOrManual());
        LibraryLocationText.Text = "微软云端 · " + (activeCloudAccount?.Email ?? activeCloudAccount?.DisplayName ?? "账号尚未识别");
        StatusText.Text = $"读取到 {list.Count} 张云端白板。选择白板并导出，完成后可离线重绘。";
    });
    private void ApplyCloudBoards(IReadOnlyList<CloudBoard> list, ExportAccount? account = null)
    {
        var wasSelecting = selecting;
        selecting = true;
        try
        {
        foreach (var sample in boards.Where(item => item.IsSample).ToArray())
        {
            if (ReferenceEquals(currentItem, sample))
            {
                current = null; currentItem = null; currentPdf = currentImage = null;
                ClearMediaViews(); BoardTitle.Text = "选择一张白板"; BoardMetadataText.Text = ""; DiagnosticsText.Text = "请选择云端白板并导出。";
            }
            boards.Remove(sample);
        }
        var listedIds = list.Select(board => board.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        void Detach(BoardItem item)
        {
            item.Cloud = null;
            if (item.Document is null && item.LibraryEntry is null && item.MediaPath is null) boards.Remove(item);
            else item.Subtitle = (item.Document is { } document ? StatusLabel(document) : "已保存的导出") + " · 本地备份";
        }
        foreach (var previous in boards.Where(item => item.Cloud is not null).ToArray())
        {
            var same = account is not null ? SameAccount(previous, account) : CloudBoardAccount(previous) is null && previous.Document is null;
            var temporaryUpgrade = account is not null && CanBindCloudAccount(previous, account) &&
                previous.Document is null && previous.LibraryEntry is null && previous.MediaPath is null;
            if (!listedIds.Contains(previous.Id) || !same && !temporaryUpgrade) Detach(previous);
        }
        foreach (var board in list.DistinctBy(board => board.Id, StringComparer.OrdinalIgnoreCase))
        {
            var existing = boards.FirstOrDefault(item => item.Id.Equals(board.Id, StringComparison.OrdinalIgnoreCase) &&
                (account is not null ? SameAccount(item, account) : item.Cloud is not null && CloudBoardAccount(item) is null));
            existing ??= account is null ? null : boards.FirstOrDefault(item => item.Cloud is not null &&
                item.Id.Equals(board.Id, StringComparison.OrdinalIgnoreCase) && CanBindCloudAccount(item, account) &&
                item.Document is null && item.LibraryEntry is null && item.MediaPath is null);
            if (existing is null) boards.Add(new BoardItem { Id = board.Id, Title = board.Title, Cloud = board, Account = account, Subtitle = "微软云端 · 导出后可离线打开" });
            else
            {
                if (existing.Account?.Key != account?.Key)
                {
                    existing.RetryOriginals.Clear(); existing.PdfPath = existing.ImagePath = existing.HtmlPath = existing.SvgPath = null;
                    existing.ExportError = null;
                }
                existing.Cloud = board; existing.Title = board.Title; existing.Account = account ?? existing.Account;
                existing.Subtitle = existing.Document is { } document ? StatusLabel(document) + " · 微软云端" : "微软云端 · 导出后可离线打开";
            }
            var live = boards.First(item => item.Cloud is not null && item.Id.Equals(board.Id, StringComparison.OrdinalIgnoreCase) &&
                (account is not null ? SameAccount(item, account) : CloudBoardAccount(item) is null));
            foreach (var duplicate in boards.Where(item => item.Cloud is not null && !ReferenceEquals(item, live) &&
                item.Id.Equals(board.Id, StringComparison.OrdinalIgnoreCase)).ToArray()) Detach(duplicate);
        }
        }
        finally { selecting = wasSelecting; }
    }
    private async void Logout_Click(object sender, RoutedEventArgs e) => await RunAsync(async _ =>
    {
        if (cloud is not null) await cloud.LogoutAsync();
        activeCloudAccount = null; AccountDirectoryBox.Text = ""; AccountDirectoryBox.IsEnabled = true; AccountInfoText.Text = "账号目录：本地导入";
        foreach (var board in boards.Where(b => b.Cloud is not null).ToArray())
            if (board.Document is null && board.LibraryEntry is null) boards.Remove(board); else board.Cloud = null;
        StatusText.Text = "已退出账号。本地备份与预览仍可使用。";
    });

    private void Search_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    { if (BoardList is not null) CollectionViewSource.GetDefaultView(boards)?.Refresh(); }
    private void SelectAll_Click(object sender, RoutedEventArgs e) => BoardList.SelectAll();
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => BoardList.UnselectAll();
    private async void Board_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!initialized || busy || selecting || BoardList.SelectedItem is not BoardItem item) return;
        libraryPreviewCancellation?.Cancel();
        libraryPreviewCancellation?.Dispose();
        var previewCancellation = libraryPreviewCancellation = new CancellationTokenSource();
        try
        {
            await PreviewBoardAsync(item, previewCancellation.Token);
        }
        catch (OperationCanceledException) when (previewCancellation.IsCancellationRequested) { }
        catch (Exception ex) { StatusText.Text = "预览失败：" + ex.Message; }
    }
    internal async Task ShowDocumentAsync(BoardDocument document, CancellationToken ct = default)
    {
        current = document;
        offlineVisualPath = null; SceneTab.Header = "原始重绘";
        ClearMediaViews();
        currentItem = boards.FirstOrDefault(item => ReferenceEquals(item.Document, document));
        if (currentItem is not null && !ReferenceEquals(BoardList.SelectedItem, currentItem)) SelectQuietly(currentItem);
        currentPdf = File.Exists(currentItem?.PdfPath) ? currentItem!.PdfPath : null;
        currentImage = File.Exists(currentItem?.ImagePath) ? currentItem!.ImagePath : null;
        BoardTitle.Text = document.Title;
        BoardMetadataText.Text = currentItem is not null ? BoardDetails(currentItem) : $"白板 ID：{document.Id} · {ModifiedLabel(document.LastModifiedUtc)}";
        ImageScale.ScaleX = ImageScale.ScaleY = 1;
        var diagnostics = document.Scene["diagnostics"];
        var warnings = diagnostics?["items"] as JsonArray;
        DiagnosticsText.Text = StatusLabel(document) + (warnings?.Count > 0 ? "\n\n" + string.Join("\n", warnings.Take(12).Select(w => "• " + (w?["message"]?.GetValue<string>() ?? w?["code"]?.GetValue<string>() ?? "内容警告"))) : "\n所有引用资源已保存到本机。");
        if (document.Status == "partial") DiagnosticsText.Text += "\n\n其余内容仍可导出。上述图片、对象或其他内容未能完整还原，原始文件已保留。";
        if (currentItem?.ExportError is { Length: > 0 } error) DiagnosticsText.Text += "\n\n导出失败：" + error;
        if (currentItem?.LibraryWarning is { } warning) DiagnosticsText.Text += "\n\n" + warning;
        UpdatePreviewActions();
        PreviewTabs.SelectedItem = SceneTab;
        PreviewTabs.UpdateLayout();
        await sceneHost.LoadAsync(document, ct);
        if (!ReferenceEquals(current, document)) return;
        await sceneHost.EvaluateAsync("window.wb.fit()", ct);
    }
    private static string StatusLabel(BoardDocument document) =>
        (document.ElementCount == 0 && (document.Scene["diagnostics"]?["items"] as JsonArray)?.Any(item => item?["code"]?.GetValue<string>() == "EMPTY_CANVAS") == true
            ? "空画布（微软源导出）"
            : document.Status switch { "complete" => "完整", "partial" => "可导出 · 有内容警告", _ => "无法重绘，仅保留源文件" }) + $" · {document.ElementCount} 个对象";
    private static string ModifiedLabel(DateTimeOffset? modified) => modified is { } date
        ? "最后编辑：" + date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
        : "最后编辑：尚未读取到";
    private static string BoardDetails(BoardItem item) => $"白板 ID：{item.Id} · {ModifiedLabel(item.Document is { } document ? document.LastModifiedUtc : item.Cloud?.Modified)}" +
        (item.Account is { } account ? "\n账号：" + (account.Email ?? account.DisplayName) : "");

    private IEnumerable<ExportFormat> SelectedFormats()
    {
        if (FormatArchive.IsChecked == true) yield return ExportFormat.Archive;
        if (FormatJson.IsChecked == true) yield return ExportFormat.Json;
        if (FormatPdf.IsChecked == true) yield return ExportFormat.Pdf;
        if (FormatPng.IsChecked == true) yield return ExportFormat.Png;
        if (FormatJpeg.IsChecked == true) yield return ExportFormat.Jpeg;
        if (FormatSvg.IsChecked == true) yield return ExportFormat.Svg;
        if (FormatHtml.IsChecked == true) yield return ExportFormat.Html;
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var selected = BoardList.SelectedItems.Cast<BoardItem>().Where(b => b.Document is not null || b.Cloud is not null ||
            b.LibraryEntry?.SourceCandidates.Any(path => Path.GetExtension(path).ToLowerInvariant() is ".wbarchive" or ".json" or ".zip") == true).ToArray();
        if (selected.Length == 0) { StatusText.Text = "请先选择至少一张白板。"; return; }
        await RunAsync(ct => ExportBatchAsync(selected, ct));
    }
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async ct =>
        {
            var retry = failed.ToArray();
            if (retry.Length == 0)
            {
                var root = Path.GetFullPath(OutputBox.Text);
                var pending = new List<BoardItem>();
                if (NeedsCloudAccountBindingForRetry(boards))
                {
                    await EnsureCloudAsync(); PreviewTabs.SelectedItem = LoginTab;
                    var fresh = await cloud!.RefreshBoardsAsync(ct);
                    await ReadCurrentAccountAsync(ct);
                    var account = CurrentAccountOrManual() ?? throw new InvalidOperationException("无法识别当前登录账号。请在账号目录名称中填写该账号的独立名称后再次重试。");
                    ApplyCloudBoards(fresh, account);
                }
                foreach (var group in boards.GroupBy(item => BoardAccount(item).Key))
                {
                    var accountFolder = await AccountDirectoryService.ResolveAsync(root, BoardAccount(group.First()), ct);
                    var state = await ReadStateAsync(Path.Combine(accountFolder, "export-state.json"), ct);
                    pending.AddRange(group.Where(item => state[EntryKey(item.Id)]?["status"]?.GetValue<string>() is "failed" or "running"));
                }
                retry = pending.ToArray();
            }
            if (retry.Length == 0) { StatusText.Text = "当前没有失败任务；重新打开软件后，请先读取白板列表或导入备份。"; return; }
            await ExportBatchAsync(retry, ct, retrySources: true);
        });
    }
    private async Task ExportAccountBatchAsync(IReadOnlyList<BoardItem> selected, string accountOutput, CancellationToken ct, bool retrySources = false)
    {
        var formats = SelectedFormats().ToArray();
        if (formats.Length == 0) throw new InvalidOperationException("请至少选择一种文件格式。");
        var output = Path.GetFullPath(accountOutput);
        Directory.CreateDirectory(output);
        var imageScale = double.Parse(((System.Windows.Controls.ComboBoxItem)ImageScaleBox.SelectedItem).Tag.ToString()!, CultureInfo.InvariantCulture);
        var layout = Enum.Parse<PdfLayout>(((System.Windows.Controls.ComboBoxItem)PdfLayoutBox.SelectedItem).Tag.ToString()!);
        var transparent = TransparentBox.IsChecked == true;
        var settingsKey = "image-scale-v7-accounts|" + string.Join(',', formats) + "|" + imageScale.ToString("R", CultureInfo.InvariantCulture) + $"|{layout}|{transparent}";
        var statePath = Path.Combine(output, "export-state.json");
        var state = await ReadStateAsync(statePath, ct);
        var runId = ExportTaskState.Begin(state, selected.Select(item => item.Id), settingsKey);
        await SaveStateAsync(statePath, state, ct);
        var failedBefore = failed.Count; int completed = 0; int partial = 0; int empty = 0;
        foreach (var item in selected)
        {
            ct.ThrowIfCancellationRequested();
            var entryKey = EntryKey(item.Id);
            var signature = item.Cloud is not null
                ? CloudCompletionSignature(item.Cloud, runId)
                : ExportTaskState.DocumentFingerprint(item.Document!);
            signature += "|" + settingsKey;
            var saved = state[entryKey];
            if (saved?["signature"]?.GetValue<string>() == signature && saved?["status"]?.GetValue<string>() == "complete" && saved?["contentStatus"]?.GetValue<string>() == "complete" &&
                (item.Cloud is null || saved?["sourceEmpty"]?.GetValue<bool>() != true) &&
                await CompletedFilesValidAsync(saved, output, ct))
            {
                await RestoreCompletedBoardAsync(item, saved!, ct);
                completed++; if (saved?["sourceEmpty"]?.GetValue<bool>() == true) empty++;
                TaskProgress.Value = 100.0 * (completed + failed.Count - failedBefore) / selected.Count;
                StatusText.Text = "跳过已完成白板：" + item.Title; continue;
            }
            var outputFiles = new List<string>();
            var formatResults = new JsonObject();
            var captureIncomplete = false;
            var recoveredCapturedImages = 0;
            var record = new JsonObject { ["title"] = item.Title, ["signature"] = signature, ["status"] = "running", ["accountKey"] = BoardAccount(item).Key };
            item.ExportError = null;
            try
            {
                StatusText.Text = $"[{completed + failed.Count + 1}/{selected.Count}] 正在导出 {item.Title}";
                var folder = ResolveBoardFolder(output, item.Id, item.Title, saved);
                var stem = Path.GetFileName(folder);
                Directory.CreateDirectory(folder);
                record["outputFolder"] = folder;
                record["cloudModified"] = item.Cloud?.Modified?.ToUniversalTime().ToString("O");
                state[entryKey] = record;
                await SaveStateAsync(statePath, state, ct);
                if (item.Cloud is not null)
                {
                    DateTimeOffset? capturedAt = null;
                    var cached = retrySources || saved?["status"]?.GetValue<string>() == "running" ? await FindRetrySourceAsync(saved, folder, item, ct) : null;
                    if (cached is not null)
                    {
                        StatusText.Text = "正在重新解析已下载的原始 ZIP：" + item.Title;
                        item.Document = await importer.ImportAsync(cached, ct);
                    }
                    var incompleteSource = (item.Document?.Scene["diagnostics"]?["items"] as JsonArray)?.Any(d => d?["code"]?.GetValue<string>() is "EMPTY_CANVAS" or "IMAGE_NOT_READY") == true;
                    if (cached is null || incompleteSource)
                    {
                        if (incompleteSource) StatusText.Text = "已保存的微软源包为空或图片未加载，等待白板加载后重新获取：" + item.Title;
                        await EnsureCloudAsync(); PreviewTabs.SelectedItem = LoginTab;
                        CloudCapture capture;
                        try { capture = await cloud!.CaptureAsync(item.Cloud, Path.Combine(folder, "source"), ct); }
                        catch (CloudCaptureIncompleteException ex)
                        {
                            // Preserve verified source bytes and export available contents,
                            // then leave the task retryable if actual image recovery fails.
                            capture = ex.Capture;
                            captureIncomplete = true;
                        }
                        item.Document = await importer.ImportAsync(capture.ZipPath, ct);
                        capturedAt = DateTimeOffset.UtcNow;
                        if (capture.Modified is { } capturedModified)
                            item.Cloud = item.Cloud with { Modified = capturedModified, ModifiedSource = capture.ModifiedSource };
                        foreach (var source in capture.SourceResponses) item.Document.Originals["responses/" + Path.GetFileName(source.Key)] = source.Value;
                        if (capture.Resources is not null) recoveredCapturedImages += ResourceRecoveryService.ApplyCapturedAssets(item.Document, capture.Resources);
                    }
                    else if (item.Document is { } cachedDocument)
                    {
                        // Retain supplementary responses from the in-memory capture while
                        // normalizing the unchanged original ZIP with the current parser.
                        foreach (var source in item.RetryOriginals) cachedDocument.Originals.TryAdd(source.Key, source.Value);
                    }
                    var cloudDocument = item.Document ?? throw new InvalidOperationException("白板尚未下载。");
                    cloudDocument.Scene["board"]!["id"] = item.Id;
                    cloudDocument.Scene["board"]!["title"] = item.Title;
                    cloudDocument.Scene["board"]!["account"] = BoardAccount(item).ToJson();
                    BoardMetadata.ApplyCloudMetadata(cloudDocument, item.Cloud.Modified, item.Cloud.ModifiedSource, capturedAt);
                    record["signature"] = CloudCompletionSignature(item.Cloud, runId) + "|" + settingsKey;
                    item.PdfPath = item.ImagePath = null;
                }
                else if (retrySources && item.Document?.Status == "failed" && File.Exists(item.Document.SourcePath))
                    item.Document = await importer.ImportAsync(item.Document.SourcePath!, ct);
                var document = item.Document ?? throw new InvalidOperationException("白板尚未下载。");
                document.Scene["board"]!["account"] = BoardAccount(item).ToJson();
                ResourceRecoveryService.ValidateLocalAssets(document);
                if (item.Cloud is not null && cloud is not null) recoveredCapturedImages += await cloud.ResolveAssetsAsync(document, ct);
                await RecoverResourcesAsync(document, ct);
                item.RetryOriginals.Clear();
                foreach (var source in document.Originals.Where(p => p.Key.StartsWith("responses/", StringComparison.OrdinalIgnoreCase))) item.RetryOriginals[source.Key] = source.Value;
                record["contentStatus"] = document.Status;
                record["lastModifiedUtc"] = document.LastModifiedUtc?.ToUniversalTime().ToString("O");
                record["lastModifiedSource"] = document.Scene["board"]?["lastModifiedSource"]?.DeepClone();
                record["sceneFingerprint"] = ExportTaskState.SceneFingerprint(document);
                record["elementCount"] = document.ElementCount;
                var sourceEmpty = document.ElementCount == 0 && (document.Scene["diagnostics"]?["items"] as JsonArray)?.Any(d => d?["code"]?.GetValue<string>() == "EMPTY_CANVAS") == true;
                record["sourceEmpty"] = sourceEmpty;
                record["formats"] = formatResults;
                record["cloudModified"] = item.Cloud?.Modified?.ToUniversalTime().ToString("O");
                if (document.SourcePath is { } sourcePath && File.Exists(sourcePath))
                { record["sourcePath"] = sourcePath; record["sourceHash"] = await HashFileAsync(sourcePath, ct); }
                state[entryKey] = record;
                await SaveStateAsync(statePath, state, ct);
                var errors = new List<string>();
                var previousDataFiles = new JsonArray();
                record["previousDataFiles"] = previousDataFiles;
                if (captureIncomplete && (document.Status != "complete" || recoveredCapturedImages == 0))
                    errors.Add("微软重采未能补齐原始图片，已保存最后一份有效 ZIP；尚未完成完整内容导出，可重试。");
                if (item.Cloud is not null && (document.Scene["diagnostics"]?["items"] as JsonArray)?.Any(d => d?["code"]?.GetValue<string>() is "IMAGE_NOT_READY" or "IMAGE_CONTENT_MISSING") == true)
                    errors.Add("微软图片仍未加载，已保存原始 ZIP 和其余内容；请等待在线图片可见后重试。");
                foreach (var format in formats)
                {
                    ct.ThrowIfCancellationRequested();
                    var extension = format switch { ExportFormat.Archive => ".wbarchive", ExportFormat.Json => ".json", ExportFormat.Pdf => ".pdf", ExportFormat.Png => ".png", ExportFormat.Jpeg => ".jpg", ExportFormat.Svg => ".svg", _ => ".html" };
                    var path = Path.Combine(folder, stem + extension);
                    try
                    {
                        var unchangedData = File.Exists(path) && (format switch
                        {
                            ExportFormat.Archive => await ArchiveService.MatchesDocumentAsync(document, path, ct),
                            ExportFormat.Json => await ArchiveService.SceneJsonMatchesAsync(document, path, ct),
                            _ => false
                        });
                        if (!unchangedData && (format is ExportFormat.Archive or ExportFormat.Json) && File.Exists(path))
                        {
                            // An older official ZIP can contain expiring image URLs.
                            // Keep its captured bytes and JSON mapping before replacing
                            // the latest export. JSON siblings share content-addressed assets.
                            var previousPath = Path.Combine(folder, stem + ".previous-" + Guid.NewGuid().ToString("N")[..12] + extension);
                            await PreserveDataVersionAsync(path, previousPath, ct);
                            previousDataFiles.Add(previousPath);
                        }
                        var result = unchangedData ? await ExistingDataFilesAsync(format, path, ct)
                            : await exporter.ExportAsync(document, new ExportOptions(format, imageScale, layout, transparent), path, ct);
                        outputFiles.AddRange(result);
                        formatResults[format.ToString()] = new JsonObject { ["status"] = "complete", ["files"] = new JsonArray(result.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()) };
                        if (format == ExportFormat.Pdf) item.PdfPath = result[0];
                        if (format == ExportFormat.Html) item.HtmlPath = result[0];
                        if (format == ExportFormat.Svg) item.SvgPath = result[0];
                        if (format == ExportFormat.Png || (format == ExportFormat.Jpeg && !formats.Contains(ExportFormat.Png))) item.ImagePath = result[0];
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        errors.Add(format + "：" + ex.Message);
                        formatResults[format.ToString()] = new JsonObject { ["status"] = "failed", ["error"] = ex.Message };
                    }
                    record["files"] = new JsonArray(outputFiles.Distinct(StringComparer.OrdinalIgnoreCase).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
                    await SaveStateAsync(statePath, state, ct);
                }
                var hashes = new JsonObject();
                foreach (var file in outputFiles.Distinct(StringComparer.OrdinalIgnoreCase)) hashes[file] = await HashFileAsync(file, ct);
                record["files"] = new JsonArray(outputFiles.Distinct(StringComparer.OrdinalIgnoreCase).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
                record["hashes"] = hashes;
                record["formats"] = formatResults;
                if (errors.Count > 0) throw new IOException(string.Join("\n", errors));
                record["status"] = "complete";
                record["completedUtc"] = DateTimeOffset.UtcNow.ToString("O");
                if (document.Status != "complete") partial++;
                if (sourceEmpty) empty++;
                completed++; item.Subtitle = StatusLabel(document);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed.Add(item); item.ExportError = ex.Message; item.Subtitle = "导出失败 · " + ex.Message.Split('\n')[0];
                record["status"] = "failed"; record["error"] = ex.Message; record["formats"] = formatResults;
                state[entryKey] = record;
                StatusText.Text = item.Title + " 导出失败：" + ex.Message;
            }
            await SaveStateAsync(statePath, state, ct);
            TaskProgress.Value = 100.0 * (completed + failed.Count - failedBefore) / selected.Count;
        }
        ExportTaskState.Complete(state);
        await SaveStateAsync(statePath, state, ct);
        var summary = $"导出完成：{completed} 张成功，{failed.Count} 张失败，{partial} 张含内容警告。输出目录：{output}";
        if (empty > 0) summary += $"\n其中 {empty} 张微软源导出为空画布；若在线有内容，请等待加载完成后重新获取。";
        var last = (BoardList.SelectedItem as BoardItem)?.Document ?? selected.LastOrDefault(b => b.Document is not null)?.Document;
        try { if (last is not null) await ShowDocumentAsync(last, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { summary += "（预览已取消）"; }
        catch (Exception ex) { summary += "\n文件已保存；预览加载失败：" + ex.Message; }
        StatusText.Text = summary;
    }
    private static string CloudCompletionSignature(CloudBoard board, string runId)
    {
        const string captionPrefix = "microsoft-gallery:edited-caption";
        const string exactCaptionPrefix = captionPrefix + "-iso";
        var source = board.ModifiedSource;
        var isCaption = source?.StartsWith(captionPrefix, StringComparison.Ordinal) == true;
        var exactCaption = source == exactCaptionPrefix || source?.StartsWith(exactCaptionPrefix + ";", StringComparison.Ordinal) == true;
        // Human-readable gallery captions round away edits within a minute/second.
        // Keep their date for display, but require a new acquisition in a new batch.
        return ExportTaskState.CloudSignature(isCaption && !exactCaption ? null : board.Modified, runId);
    }
    private static string EntryKey(string id) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..20];
    private static string ResolveBoardFolder(string output, string id, string title, JsonNode? saved)
    {
        var root = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var suffix = "__" + SafeName(id);
        string? Verified(string? candidate)
        {
            if (candidate is null) return null;
            try
            {
                var full = Path.GetFullPath(candidate);
                return Path.GetDirectoryName(full)?.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) == true &&
                    Path.GetFileName(full).EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }
        if (Verified(saved?["outputFolder"]?.GetValue<string>()) is { } folder) return folder;
        if (saved?["files"] is JsonArray files)
            foreach (var file in files)
                if (Verified(Path.GetDirectoryName(file?.GetValue<string>() ?? "")) is { } existing) return existing;
        var matching = Directory.GetDirectories(output).Where(path => Path.GetFileName(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matching.Length == 1) return matching[0];
        return Path.Combine(output, SafeName(title) + suffix);
    }
    private async Task RestoreCompletedBoardAsync(BoardItem item, JsonNode saved, CancellationToken ct)
    {
        var paths = (saved["files"] as JsonArray ?? []).Select(file => file?.GetValue<string>()).OfType<string>().ToArray();
        if (item.Document is null && paths.FirstOrDefault(path => Path.GetExtension(path).Equals(".wbarchive", StringComparison.OrdinalIgnoreCase)) is { } archive)
            item.Document = await importer.ImportAsync(archive, ct);
        else if (item.Document is null && paths.FirstOrDefault(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) is { } json)
            item.Document = await importer.ImportAsync(json, ct);
        item.PdfPath = paths.FirstOrDefault(path => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        item.HtmlPath = paths.FirstOrDefault(path => Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase));
        item.SvgPath = paths.FirstOrDefault(path => Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase));
        item.ImagePath = paths.FirstOrDefault(path => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)) ??
            paths.FirstOrDefault(path => Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase));
        item.ExportError = null;
        if (item.Document is { } document) item.Subtitle = StatusLabel(document);
    }
    private static async Task<IReadOnlyList<string>> ExistingDataFilesAsync(ExportFormat format, string path, CancellationToken ct)
    {
        if (format == ExportFormat.Archive) return [path];
        var scene = JsonNode.Parse(await File.ReadAllTextAsync(path, ct));
        var directory = Path.GetDirectoryName(path)!;
        var files = new List<string> { path };
        foreach (var asset in (scene?["assets"] as JsonObject ?? []))
            if (asset.Value?["path"]?.GetValue<string>() is { } relative)
                files.Add(Path.GetFullPath(Path.Combine(directory, relative)));
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static async Task<JsonObject> ReadStateAsync(string path, CancellationToken ct)
    {
        try { return File.Exists(path) ? JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonObject ?? new() : new(); }
        catch (JsonException) { return new(); }
    }
    private static async Task<string?> FindRetrySourceAsync(JsonNode? saved, string folder, BoardItem item, CancellationToken ct)
    {
        if (saved?["cloudModified"]?.GetValue<string>() is { } previous && item.Cloud?.Modified is { } modified &&
            previous != modified.ToUniversalTime().ToString("O")) return null;
        var prefix = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = saved?["sourcePath"]?.GetValue<string>();
        if (path is null)
        {
            var sourceFolder = Path.Combine(folder, "source");
            var candidates = Directory.Exists(sourceFolder) ? Directory.GetFiles(sourceFolder, "*.zip").Where(p =>
            {
                var name = Path.GetFileNameWithoutExtension(p);
                return name.EndsWith("__" + item.Id, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(item.Id + "__", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(name.AsSpan(item.Id.Length + 2), out _);
            }).OrderByDescending(File.GetLastWriteTimeUtc).ToArray() : [];
            if (candidates.Length > 0) path = candidates[0];
        }
        if (path is null || !Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
        if (saved?["sourceHash"]?.GetValue<string>() is { } hash && hash != await HashFileAsync(path, ct)) return null;
        return path;
    }
    private static async Task SaveStateAsync(string path, JsonObject state, CancellationToken ct)
    {
        var temporary = path + ".tmp";
        try { await File.WriteAllTextAsync(temporary, state.ToJsonString(LocalWebHost.JsonOptions), ct); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task PreserveDataVersionAsync(string sourcePath, string previousPath, CancellationToken ct)
    {
        var temporary = previousPath + ".partial";
        try
        {
            await using (var source = File.OpenRead(sourcePath))
            await using (var previous = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(previous, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, previousPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string SafeName(string name)
    { var sanitized = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.'); return string.IsNullOrEmpty(sanitized) ? "whiteboard" : sanitized[..Math.Min(sanitized.Length, 65)]; }
    private void SelectQuietly(BoardItem item)
    { selecting = true; try { BoardList.SelectedItem = item; } finally { selecting = false; } }
    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)); }
    private static async Task<bool> CompletedFilesValidAsync(JsonNode? entry, string output, CancellationToken ct)
    {
        if (entry?["files"] is not JsonArray files || files.Count == 0 || entry["hashes"] is not JsonObject hashes) return false;
        var prefix = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in files)
        {
            var path = file?.GetValue<string>();
            if (path is null || !Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || hashes[path]?.GetValue<string>() != await HashFileAsync(path, ct)) return false;
        }
        return true;
    }

    private async void Preview_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!initialized || busy || previewUpdating || e.Source != PreviewTabs) return;
        previewUpdateDepth++;
        try
        {
            if (PreviewTabs.SelectedItem == PdfTab)
            {
                if (currentPdf is null && current is not null && current.Status != "failed")
                {
                    var document = current;
                    var path = Path.Combine(dataDirectory, "Preview", BoardAccount(currentItem ?? new BoardItem { Id = document.Id, Title = document.Title, Document = document }).Key[..16] + "__" + SafeName(document.Id) + ".pdf");
                    await exporter.ExportAsync(document, new ExportOptions(ExportFormat.Pdf), path);
                    if (!ReferenceEquals(current, document) || PreviewTabs.SelectedItem != PdfTab) return;
                    currentPdf = path;
                }
                if (currentPdf is not null) await ShowPdfAsync(currentPdf);
            }
            if (PreviewTabs.SelectedItem == ImageTab)
            {
                if (currentImage is null && current is not null && current.Status != "failed")
                {
                    var document = current;
                    var path = Path.Combine(dataDirectory, "Preview", BoardAccount(currentItem ?? new BoardItem { Id = document.Id, Title = document.Title, Document = document }).Key[..16] + "__" + SafeName(document.Id) + ".png");
                    await exportHost.LoadAsync(document);
                    var metrics = await exportHost.EvaluateAsync("window.wb.getExportMetrics()");
                    var bounds = metrics?["bounds"];
                    var previewScale = Math.Min(1, 2048 / Math.Max(bounds?["width"]?.GetValue<double>() ?? 800, bounds?["height"]?.GetValue<double>() ?? 600));
                    await exporter.ExportAsync(document, new ExportOptions(ExportFormat.Png, previewScale), path);
                    if (!ReferenceEquals(current, document) || PreviewTabs.SelectedItem != ImageTab) return;
                    currentImage = path;
                }
                if (currentImage is not null) ShowImage(currentImage);
            }
        }
        catch (Exception ex) { StatusText.Text = "预览生成失败：" + ex.Message; }
        finally { previewUpdateDepth--; }
    }
    internal async Task ShowPdfAsync(string path)
    {
        var expectedItem = currentItem;
        var expectedDocument = current;
        var generation = ++pdfPreviewGeneration;
        previewUpdateDepth++;
        PreviewTabs.SelectedItem = PdfTab;
        PreviewTabs.UpdateLayout();
        try
        {
        if (!pdfInitialized)
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataDirectory, "Browser", "Pdf"));
            await PdfView.EnsureCoreWebView2Async(environment);
            PdfView.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            PdfView.CoreWebView2.Settings.IsWebMessageEnabled = false;
            PdfView.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            PdfView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            PdfView.CoreWebView2.WebResourceRequested += (_, e) => { if (e.Request.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) e.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Offline", ""); };
            pdfInitialized = true;
        }
        if (generation != pdfPreviewGeneration || !ReferenceEquals(currentItem, expectedItem) || !ReferenceEquals(current, expectedDocument)) return;
        PdfView.ZoomFactor = 1;
        // Chromium can retain an old PDF at the same file URL after an atomic replacement.
        // A unique query creates a fresh document; the actual file path stays unchanged.
        var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var navigationUrl = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?revision=" + Guid.NewGuid().ToString("N") + "#view=Fit";
        ulong? navigationId = null;
        void Starting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        { if (args.Uri == navigationUrl || args.Uri == navigationUrl.Split('#')[0]) navigationId = args.NavigationId; }
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        { if (navigationId == args.NavigationId) navigation.TrySetResult(args.IsSuccess); }
        PdfView.CoreWebView2.NavigationStarting += Starting;
        PdfView.CoreWebView2.NavigationCompleted += Completed;
        try
        {
            PdfView.CoreWebView2.Navigate(navigationUrl);
            if (!await navigation.Task.WaitAsync(TimeSpan.FromSeconds(10))) return;
            if (generation != pdfPreviewGeneration || !ReferenceEquals(currentItem, expectedItem) || !ReferenceEquals(current, expectedDocument)) return;
            try { await PdfPreviewService.EnsureFitPageAsync(PdfView.CoreWebView2); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        }
        finally { PdfView.CoreWebView2.NavigationStarting -= Starting; PdfView.CoreWebView2.NavigationCompleted -= Completed; }
        if (generation != pdfPreviewGeneration || !ReferenceEquals(currentItem, expectedItem) || !ReferenceEquals(current, expectedDocument)) return;
        currentPdf = path;
        if (currentItem?.Document is not null) currentItem.PdfPath = path;
        }
        finally { previewUpdateDepth--; }
    }
    internal void ShowImage(string path)
    {
        int width, height;
        // URI-based WPF decoding can reuse cached pixels and retain a file handle.
        // Read dimensions from a short-lived stream and load bounded preview pixels fully.
        using (var headerStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var header = BitmapDecoder.Create(headerStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            width = header.PixelWidth; height = header.PixelHeight;
        }
        var bitmap = new BitmapImage();
        using (var imageStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = imageStream;
            if (Math.Max(width, height) > 4096)
            { if (width >= height) bitmap.DecodePixelWidth = 4096; else bitmap.DecodePixelHeight = 4096; }
            bitmap.EndInit(); bitmap.Freeze();
        }
        ImagePreview.Source = bitmap; ImagePreview.Width = bitmap.Width; ImagePreview.Height = bitmap.Height;
        ImageDetails.Text = $"{width} × {height} 像素";
        currentImage = path; PreviewTabs.SelectedItem = ImageTab;
        PreviewTabs.UpdateLayout(); FitImage();
        if (currentItem?.Document is not null) currentItem.ImagePath = path;
    }
    private void ClearMediaViews()
    {
        pdfPreviewGeneration++;
        SceneView.Visibility = current is null ? Visibility.Hidden : Visibility.Visible;
        ImagePreview.Source = null;
        ImageDetails.Text = "";
        if (pdfInitialized) PdfView.CoreWebView2.Navigate("about:blank");
    }
    private void FitImage()
    {
        if (ImagePreview.Source is not BitmapSource image) return;
        var width = ImageScroller.ViewportWidth > 0 ? ImageScroller.ViewportWidth : ImageScroller.ActualWidth;
        var height = ImageScroller.ViewportHeight > 0 ? ImageScroller.ViewportHeight : ImageScroller.ActualHeight;
        var scale = Math.Clamp(Math.Min(Math.Max(1, width - 8) / image.Width, Math.Max(1, height - 8) / image.Height), .0001, 10);
        ImageScale.ScaleX = ImageScale.ScaleY = scale;
    }
    private async void Fit_Click(object sender, RoutedEventArgs e)
    { if (!initialized) return; if (PreviewTabs.SelectedItem == ImageTab) FitImage(); else if (PreviewTabs.SelectedItem == PdfTab && pdfInitialized) { PdfView.ZoomFactor = 1; await PdfPreviewService.EnsureFitPageAsync(PdfView.CoreWebView2); } else await sceneHost.EvaluateAsync("window.wb.fit()"); }
    private async void ZoomIn_Click(object sender, RoutedEventArgs e) => await ZoomAsync(1.25);
    private async void ZoomOut_Click(object sender, RoutedEventArgs e) => await ZoomAsync(0.8);
    private async Task ZoomAsync(double factor)
    {
        if (!initialized) return;
        if (PreviewTabs.SelectedItem == ImageTab) { ImageScale.ScaleX = ImageScale.ScaleY = Math.Clamp(ImageScale.ScaleX * factor, .0001, 10); return; }
        if (PreviewTabs.SelectedItem == PdfTab && pdfInitialized) { PdfView.ZoomFactor = Math.Clamp(PdfView.ZoomFactor * factor, .25, 5); return; }
        await sceneHost.EvaluateAsync("window.wb.setZoom(window.wb.getZoom()*" + factor.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
    }
    private void Image_MouseWheel(object sender, MouseWheelEventArgs e)
    { if (Keyboard.Modifiers != ModifierKeys.Control) return; e.Handled = true; ImageScale.ScaleX = ImageScale.ScaleY = Math.Clamp(ImageScale.ScaleX * (e.Delta > 0 ? 1.15 : .87), .0001, 10); }
    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    { var dialog = new OpenFolderDialog { Title = "选择导出目录" }; if (dialog.ShowDialog(this) == true) OutputBox.Text = dialog.FolderName; }
    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    { try { var path = Path.GetFullPath(OutputBox.Text); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }); } catch (Exception ex) { StatusText.Text = ex.Message; } }
    private void Cancel_Click(object sender, RoutedEventArgs e) => operation?.Cancel();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy) { operation?.Cancel(); e.Cancel = true; StatusText.Text = "正在取消任务，完成后可关闭窗口。"; return; }
        cloud?.Dispose();
        libraryPreviewCancellation?.Cancel(); libraryPreviewCancellation?.Dispose();
        SceneView.Dispose(); ExportView.Dispose(); PdfView.Dispose(); CloudView.Dispose();
    }

    internal LocalWebHost SceneHost => sceneHost;
    internal bool PdfPreviewOnly => arguments.Contains("--pdf-preview-only");
    internal bool PdfPreviewSmall => arguments.Contains("--pdf-preview-small");
    internal LocalWebHost ExportHost => exportHost;
    internal ImportService Importer => importer;
    internal async Task<int> RecoverResourcesAsync(BoardDocument document, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ResourceRecoveryService.ValidateLocalAssets(document);
        var count = await resourceRecovery.ResolvePublicAssetsAsync(document, ct);
        ResourceRecoveryService.ValidateLocalAssets(document);
        ct.ThrowIfCancellationRequested();
        ResourceRecoveryService.RedactCredentialSources(document);
        return count;
    }
    internal DesktopExportService Exporter => exporter;
    internal async Task<CloudWhiteboardService> GetCloudServiceAsync()
    {
        PreviewTabs.SelectedItem = LoginTab;
        PreviewTabs.UpdateLayout();
        await EnsureCloudAsync();
        return cloud!;
    }
    internal void ChangeTestViewport() { Width = 1000; Height = 680; SceneView.ZoomFactor = 1.4; }
    internal static async Task TestRetrySourceAsync(string sourceZip, string directory)
    {
        const string id = "00000000-0000-0000-0000-000000000077";
        var item = new BoardItem { Id = id, Title = "source-resume", Cloud = new CloudBoard(id, "source-resume", DateTimeOffset.Parse("2026-10-08T00:00:00Z")) };
        var sourceFolder = Path.Combine(directory, "source"); Directory.CreateDirectory(sourceFolder);
        var legacy = Path.Combine(sourceFolder, "source-resume__" + id + ".zip"); File.Copy(sourceZip, legacy, true);
        var saved = new JsonObject { ["status"] = "failed" };
        if (await FindRetrySourceAsync(saved, directory, item, CancellationToken.None) != legacy)
            throw new InvalidOperationException("旧失败记录无法找到已下载的官方源 ZIP。");
        saved["sourcePath"] = legacy; saved["sourceHash"] = await HashFileAsync(legacy, CancellationToken.None);
        if (await FindRetrySourceAsync(saved, directory, item, CancellationToken.None) != legacy)
            throw new InvalidOperationException("有效源哈希无法复用。");
        saved["sourceHash"] = "changed";
        if (await FindRetrySourceAsync(saved, directory, item, CancellationToken.None) is not null)
            throw new InvalidOperationException("被修改的源 ZIP 被复用。");
        saved.Remove("sourceHash"); saved["sourcePath"] = sourceZip;
        if (await FindRetrySourceAsync(saved, directory, item, CancellationToken.None) is not null)
            throw new InvalidOperationException("备份目录之外的源路径被复用。");
        saved["sourcePath"] = legacy; saved["cloudModified"] = "2026-10-07T00:00:00.0000000+00:00";
        if (await FindRetrySourceAsync(saved, directory, item, CancellationToken.None) is not null)
            throw new InvalidOperationException("云端修改后的旧源 ZIP 被复用。");
        saved.Remove("sourcePath"); saved.Remove("cloudModified");
        var revision = Path.Combine(sourceFolder, id + "__" + Guid.NewGuid().ToString("N") + ".zip");
        File.Copy(sourceZip, revision);
        File.SetLastWriteTimeUtc(revision, File.GetLastWriteTimeUtc(legacy).AddSeconds(2));
        if (await FindRetrySourceAsync(saved, directory, item, CancellationToken.None) != revision)
            throw new InvalidOperationException("中断后未找到最新已验证的独立源 ZIP。");
    }
    internal async Task TestBatchRecoveryAsync(BoardDocument document, string directory)
    {
        var previous = OutputBox.Text;
        var item = new BoardItem { Id = document.Id, Title = "batch-retry-test", Document = document };
        var accountDirectory = await AccountDirectoryService.ResolveAsync(directory, ExportAccount.Local);
        boards.Add(item);
        try
        {
            OutputBox.Text = directory;
            FormatArchive.IsChecked = FormatJson.IsChecked = FormatPdf.IsChecked = FormatPng.IsChecked = FormatHtml.IsChecked = true;
            FormatJpeg.IsChecked = FormatSvg.IsChecked = false;
            ImageScaleBox.SelectedIndex = 4;
            var blockedImagePath = Path.Combine(accountDirectory, SafeName(item.Title) + "__" + SafeName(item.Id), SafeName(item.Title) + "__" + SafeName(item.Id) + ".png");
            Directory.CreateDirectory(blockedImagePath);
            await ExportBatchAsync([item], CancellationToken.None);
            var entry = (await ReadStateAsync(Path.Combine(accountDirectory, "export-state.json"), CancellationToken.None))[EntryKey(item.Id)];
            if (entry?["status"]?.GetValue<string>() != "failed" || entry["formats"]?["Png"]?["status"]?.GetValue<string>() != "failed" ||
                entry["formats"]?["Html"]?["status"]?.GetValue<string>() != "complete" || entry["files"] is not JsonArray files || files.Count < 4)
                throw new InvalidOperationException("单格式失败中断了其他格式，或未保留成功文件记录。");
            Directory.Delete(blockedImagePath);
            await ExportBatchAsync([item], CancellationToken.None, retrySources: true);
            entry = (await ReadStateAsync(Path.Combine(accountDirectory, "export-state.json"), CancellationToken.None))[EntryKey(item.Id)];
            if (entry?["status"]?.GetValue<string>() != "complete" || failed.Count != 0 || item.ExportError is not null)
                throw new InvalidOperationException("解除目标文件写入阻碍后重试没有恢复完成状态。");
        }
        finally { OutputBox.Text = previous; boards.Remove(item); }
    }
    internal async Task TestBoardIdentityAsync(BoardDocument template, string directory)
    {
        var previousOutput = OutputBox.Text;
        var accountDirectory = await AccountDirectoryService.ResolveAsync(directory, ExportAccount.Local);
        BoardItem? first = null, second = null;
        try
        {
            await LoadSampleAsync();
            if (!boards.Any(item => item.IsSample)) throw new InvalidOperationException("示例未明确标记。");
            ApplyCloudBoards([]);
            if (boards.Any(item => item.IsSample) || currentItem?.IsSample == true)
                throw new InvalidOperationException("读取列表后示例仍出现在列表或预览中。");
            BoardDocument Make(string id, string background)
            {
                var copy = new BoardDocument { Scene = (JsonObject)template.Scene.DeepClone() };
                copy.Scene["board"]!["id"] = id; copy.Scene["board"]!["title"] = "Untitled";
                copy.Scene["background"] = background;
                foreach (var pair in template.Assets) copy.Assets[pair.Key] = pair.Value;
                foreach (var pair in template.Originals) copy.Originals[pair.Key] = pair.Value;
                return copy;
            }
            first = new BoardItem { Id = "00000000-0000-0000-0000-000000000091", Title = "Untitled", Document = Make("00000000-0000-0000-0000-000000000091", "#ffffff") };
            second = new BoardItem { Id = "00000000-0000-0000-0000-000000000092", Title = "Untitled", Document = Make("00000000-0000-0000-0000-000000000092", "#ffeecc") };
            boards.Add(first); boards.Add(second);
            var edited = DateTimeOffset.Parse("2026-10-08T18:34:12+08:00", CultureInfo.InvariantCulture);
            var knownCloud = new CloudBoard(first.Id, first.Title, edited, ModifiedSource: "microsoft-api:modifiedAt");
            var captionCloud = knownCloud with { ModifiedSource = "microsoft-gallery:edited-caption;timezone=Asia/Shanghai;precision=minute" };
            foreach (var rounded in new[] { captionCloud, captionCloud with { ModifiedSource = "microsoft-gallery:edited-caption;timezone=Asia/Shanghai;precision=second" } })
            {
                if (CloudCompletionSignature(rounded, "batch-a") == CloudCompletionSignature(rounded, "batch-b") ||
                    CloudCompletionSignature(rounded, "batch-a") != CloudCompletionSignature(rounded, "batch-a"))
                    throw new InvalidOperationException("较低精度的列表编辑说明复用了上一批次，或中断恢复没有保留同一批次签名。");
            }
            var machineTimestampCloud = knownCloud with { ModifiedSource = "microsoft-gallery:edited-caption-iso" };
            if (CloudCompletionSignature(knownCloud, "batch-a") != CloudCompletionSignature(knownCloud, "batch-b") ||
                CloudCompletionSignature(machineTimestampCloud, "batch-a") != CloudCompletionSignature(machineTimestampCloud, "batch-b"))
                throw new InvalidOperationException("精确 API 或 ISO 编辑时间未在重复批次中保留稳定签名。");
            var changedDateLabel = false;
            first.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(BoardItem.ModifiedLabel)) changedDateLabel = true; };
            ApplyCloudBoards([knownCloud, new CloudBoard(second.Id, second.Title)], ExportAccount.Local);
            if (!changedDateLabel || first.ModifiedLabel != ModifiedLabel(edited) || second.ModifiedLabel != "最后编辑：尚未读取到")
                throw new InvalidOperationException("读取模拟云端编辑日期后列表绑定没有更新，或未知时间的提示不准确。");
            BoardMetadata.ApplyCloudMetadata(first.Document!, first.Cloud!.Modified, first.Cloud.ModifiedSource,
                DateTimeOffset.Parse("2026-10-09T00:00:00Z", CultureInfo.InvariantCulture));
            if (first.Document!.LastModifiedUtc != edited.ToUniversalTime())
                throw new InvalidOperationException("模拟 CloudBoard.Modified 未进入持久化场景元数据。");
            // This smoke check exports mocked captured documents without opening the live account.
            first.Cloud = null; second.Cloud = null;
            SelectQuietly(first);
            selecting = true; try { BoardList.SelectedItems.Add(second); } finally { selecting = false; }
            OutputBox.Text = directory;
            FormatArchive.IsChecked = FormatJson.IsChecked = FormatPdf.IsChecked = FormatPng.IsChecked = true;
            FormatJpeg.IsChecked = FormatSvg.IsChecked = FormatHtml.IsChecked = false;
            ImageScaleBox.SelectedIndex = 0;
            await ExportBatchAsync([first, second], CancellationToken.None);
            var metadataEntry = (await ReadStateAsync(Path.Combine(accountDirectory, "export-state.json"), CancellationToken.None))[EntryKey(first.Id)];
            if (metadataEntry?["lastModifiedUtc"]?.GetValue<string>() != edited.ToUniversalTime().ToString("O") ||
                metadataEntry?["lastModifiedSource"]?.GetValue<string>() != knownCloud.ModifiedSource ||
                !BoardMetadataText.Text.Contains(ModifiedLabel(edited)))
                throw new InvalidOperationException("源编辑日期未进入任务记录和当前白板信息。");
            var metadataFiles = metadataEntry!["files"]!.AsArray().Select(file => file!.GetValue<string>()).ToArray();
            foreach (var metadataPath in metadataFiles.Where(file => Path.GetExtension(file) is ".wbarchive" or ".json"))
            {
                var restored = await importer.ImportAsync(metadataPath);
                if (restored.LastModifiedUtc != edited.ToUniversalTime() || restored.Scene["board"]?["lastModifiedSource"]?.GetValue<string>() != knownCloud.ModifiedSource)
                    throw new InvalidOperationException("应用批量导出的备份或 JSON 丢失源编辑日期。");
                if (Path.GetExtension(metadataPath) == ".wbarchive")
                {
                    using var archive = System.IO.Compression.ZipFile.OpenRead(metadataPath);
                    using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
                    var manifest = JsonNode.Parse(await reader.ReadToEndAsync());
                    if (manifest?["lastModifiedUtc"]?.GetValue<string>() != edited.ToUniversalTime().ToString("O"))
                        throw new InvalidOperationException("应用导出的备份清单没有源编辑日期。");
                }
            }
            if (!ReferenceEquals(currentItem, first) || !ReferenceEquals(BoardList.SelectedItem, first) ||
                !BoardMetadataText.Text.Contains(first.Id) || currentPdf != first.PdfPath || currentImage != first.ImagePath)
                throw new InvalidOperationException("批量导出后选中白板与预览不一致。");
            var writeTime = File.GetLastWriteTimeUtc(first.PdfPath!);
            await ExportBatchAsync([first], CancellationToken.None);
            if (File.GetLastWriteTimeUtc(first.PdfPath!) != writeTime || Directory.GetFiles(directory, "*.previous-*", SearchOption.AllDirectories).Length > 0)
                throw new InvalidOperationException("相同白板重复导出没有复用已校验产物。");
            first.Title = "已重命名"; first.Document!.Scene["board"]!["title"] = first.Title;
            await ExportBatchAsync([first], CancellationToken.None);
            if (Directory.GetDirectories(accountDirectory).Count(folder => folder.EndsWith("__" + first.Id)) != 1 ||
                !Path.GetFileName(Path.GetDirectoryName(first.PdfPath!)!).StartsWith("Untitled__"))
                throw new InvalidOperationException("重命名白板重复导出创建了另一个目录。");
            var imagePath = Path.Combine(directory, "same-path.png");
            void Solid(byte red, byte green)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 0, green, red, 255 }, 4)));
                using var stream = File.Create(imagePath); encoder.Save(stream);
            }
            Solid(255, 0); ShowImage(imagePath); Solid(0, 255); ShowImage(imagePath);
            var pixel = new byte[4]; ((BitmapSource)ImagePreview.Source).CopyPixels(pixel, 4, 0);
            if (pixel[1] != 255 || pixel[2] != 0) throw new InvalidOperationException("覆盖同一路径后图片预览仍显示旧内容。");
            await ShowPdfAsync(first.PdfPath!);
            var pdfUrl = PdfView.CoreWebView2.Source;
            File.Copy(second.PdfPath!, first.PdfPath!, true);
            await ShowPdfAsync(first.PdfPath!);
            var navigationDeadline = Stopwatch.StartNew();
            while (pdfUrl == PdfView.CoreWebView2.Source && navigationDeadline.ElapsedMilliseconds < 3000) await Task.Delay(20);
            if (pdfUrl == PdfView.CoreWebView2.Source || !new Uri(PdfView.CoreWebView2.Source).LocalPath.Equals(first.PdfPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("同一路径 PDF 未使用独立版本导航。");
            var savedFolder = Path.GetDirectoryName(first.PdfPath!)!;
            var forged = new JsonObject { ["outputFolder"] = Path.Combine(Path.GetDirectoryName(directory)!, "escape__" + first.Id) };
            if (ResolveBoardFolder(accountDirectory, first.Id, "另一个标题", forged) != savedFolder)
                throw new InvalidOperationException("重复导出目录未拒绝越界记录。");
        }
        finally
        {
            OutputBox.Text = previousOutput;
            if (first is not null) boards.Remove(first); if (second is not null) boards.Remove(second);
            FormatHtml.IsChecked = true; ImageScaleBox.SelectedIndex = 4;
        }
    }
}

public sealed class BoardItem : INotifyPropertyChanged
{
    private string id = "";
    public required string Id { get => id; set { id = value; Changed(nameof(Id)); Changed(nameof(IdLabel)); } }
    private string title = "";
    public required string Title { get => title; set { title = value; Changed(nameof(Title)); } }
    private bool isSample;
    public bool IsSample { get => isSample; set { isSample = value; Changed(nameof(IdLabel)); Changed(nameof(ModifiedLabel)); } }
    public string IdLabel => IsSample ? "演示数据" : "ID：" + Id;
    private ExportAccount? account;
    public ExportAccount? Account { get => account; set { account = value; Changed(nameof(AccountLabel)); } }
    public string AccountLabel => IsSample ? "" : Account is { } value ? "账号：" + (value.Email ?? value.DisplayName) : LibraryEntry?.AccountLabel is { } label ? "目录：" + label : "";
    public ExportLibraryEntry? LibraryEntry { get; init; }
    public string? LibraryWarning { get; set; }
    public string ModifiedLabel => (Cloud?.Modified ?? Document?.LastModifiedUtc) is { } date
        ? "最后编辑：" + date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
        : IsSample || MediaPath is not null ? "" : "最后编辑：尚未读取到";
    private string subtitle = "";
    public string Subtitle { get => subtitle; set { subtitle = value; PropertyChanged?.Invoke(this, new(nameof(Subtitle))); } }
    private BoardDocument? document;
    public BoardDocument? Document { get => document; set { document = value; Changed(nameof(ModifiedLabel)); } }
    private CloudBoard? cloud;
    public CloudBoard? Cloud { get => cloud; set { cloud = value; Changed(nameof(ModifiedLabel)); } }
    public string? MediaPath { get; init; }
    public string? PdfPath { get; set; }
    public string? ImagePath { get; set; }
    public string? HtmlPath { get; set; }
    public string? SvgPath { get; set; }
    public string? ExportError { get; set; }
    internal Dictionary<string, byte[]> RetryOriginals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
}
