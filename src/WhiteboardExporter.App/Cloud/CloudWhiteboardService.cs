using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.Windows;
using WhiteboardExporter.App.Services;
using WhiteboardExporter.Core;

namespace WhiteboardExporter.App.Cloud;

public sealed record CloudBoard(string Id, string Title, DateTimeOffset? Modified = null, string? Url = null, string? ModifiedSource = null);
public sealed record CloudAccount(string Key, string DisplayName, string? Email, string Kind, string Source);
public sealed record CapturedImageResource(byte[] Bytes, string Mime);
public sealed record CloudCapture(string ZipPath, Dictionary<string, byte[]> SourceResponses, Dictionary<string, CapturedImageResource>? Resources = null, DateTimeOffset? Modified = null, string? ModifiedSource = null);
public sealed class CloudCaptureIncompleteException(CloudCapture capture, Exception? inner = null) : IOException("在线重采后仍未取得完整图片。已验证的原始 ZIP 已保留，但未报告为完整导出。请检查图片是否仍可访问后重试。", inner)
{
    public CloudCapture Capture { get; } = capture;
}

/// <summary>
/// Uses the signed-in Microsoft page and its own ZIP export controls. It never
/// requests credentials or sends API calls with a copied authentication token.
/// Selector reference: https://github.com/wyh2001/whiteboard-bulk-export (MIT).
/// </summary>
public sealed class CloudWhiteboardService : IDisposable
{
    private const string GalleryUrl = "https://whiteboard.cloud.microsoft/me/whiteboards";
    private const int ResponseLimit = 16 * 1024 * 1024;
    private const int ResponseTotalLimit = 64 * 1024 * 1024;
    private readonly WebView2 _webView;
    private readonly string _profileDirectory;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Dictionary<string, CloudBoard> _boards = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _responses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CapturedImageResource> _resources = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _responseReads = [];
    private readonly List<Window> _loginWindows = [];
    private CoreWebView2Environment? _environment;
    private Task? _initialization;
    private string? _captureBoardId;
    private int _captureGeneration;
    private int _responseBytes;
    private int _resourceBytes;
    private string? _resourceBoardId;
    private int _responseNumber;
    private bool _responseLimitReported;
    private bool _listResponseObserved;
    private CancellationTokenSource? _activeCaptureCancellation;
    private bool _disposed;
    private CloudAccount? _account;

    public CloudWhiteboardService(WebView2 webView, string profileDirectory)
    {
        ArgumentNullException.ThrowIfNull(webView);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        _webView = webView;
        _profileDirectory = Path.GetFullPath(profileDirectory);
    }

    public event Action<string>? StatusChanged;
    public CloudAccount? CurrentAccount => _account;

    public async Task InitializeAsync()
    {
        VerifyAccess();
        var initialization = _initialization ??= InitializeCoreAsync();
        try { await initialization; }
        catch
        {
            if (_initialization == initialization) _initialization = null;
            throw;
        }
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            Directory.CreateDirectory(_profileDirectory);
            _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _profileDirectory);
            await _webView.EnsureCoreWebView2Async(_environment);
            _webView.CoreWebView2.Profile.IsPasswordAutosaveEnabled = false;
            _webView.CoreWebView2.Profile.IsGeneralAutofillEnabled = false;
            _webView.CoreWebView2.WebResourceResponseReceived += OnResponseReceived;
            _webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            Report("微软登录浏览器已就绪；登录状态保存在本机专用配置目录。");
        }
        catch (Exception exception)
        {
            _initialization = null;
            throw new InvalidOperationException("无法初始化 WebView2。请确认已安装 Microsoft Edge WebView2 Runtime，且登录配置目录可以写入。", exception);
        }
    }

    public async Task LoginAsync()
    {
        await InitializeAsync();
        await _operationGate.WaitAsync();
        try
        {
            Report("请在微软页面完成登录及验证，然后点击刷新白板。软件不读取密码。");
            _webView.CoreWebView2.Navigate(GalleryUrl);
            _webView.Focus();
        }
        finally { _operationGate.Release(); }
    }

    /// <summary>Reads the active profile shown by Microsoft's own account control.</summary>
    public async Task<CloudAccount?> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            _account = null;
            if (!IsWhiteboardPage(_webView.Source?.AbsoluteUri)) return null;
            var deadline = DateTime.UtcNow.AddSeconds(12);
            do
            {
                var identity = await EvaluateAsync<AccountIdentity>(Scripts.ReadAccount, cancellationToken);
                if (CreateAccount(identity) is { } account) return _account = account;
                if (!IsWhiteboardPage(_webView.Source?.AbsoluteUri)) return null;
                await Task.Delay(250, cancellationToken);
            } while (DateTime.UtcNow < deadline);
            // The caller can create an explicitly named local profile. Unknown
            // accounts must not share a synthetic "unknown" export namespace.
            return null;
        }
        finally { _operationGate.Release(); }
    }

    /// <summary>Structural account-reader diagnostics; never returns profile or authentication values.</summary>
    public async Task<JsonObject?> GetAccountProbeAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var raw = await EvaluateAsync<JsonObject>(Scripts.ReadAccountProbe, cancellationToken);
            if (raw is null) return null;
            var probe = new JsonObject();
            foreach (var key in new[] { "whiteboardPage", "headerPresent", "galleryPresent", "getterPresent", "configurationValid", "reactFiberPresent", "committedFiberFound", "serviceContextPresent", "userInfoProviderPresent", "committedHeaderModelPresent", "headerModelValid", "currentDomPresent", "currentDomValid" })
                probe[key] = raw[key] is JsonValue value && value.TryGetValue<bool>(out var present) && present;
            foreach (var key in new[] { "committedAncestorCount", "contextDependencyCount", "committedSearchNodes" })
                probe[key] = raw[key] is JsonValue value && value.TryGetValue<int>(out var count) ? Math.Clamp(count, 0, 65535) : 0;
            var source = raw["source"] is JsonValue sourceValue && sourceValue.TryGetValue<string>(out var text) ? text : null;
            probe["source"] = source is "header-model" or "current-dom" or "configuration" ? source : "none";
            return probe;
        }
        finally { _operationGate.Release(); }
    }

    private static CloudAccount? CreateAccount(AccountIdentity? identity)
    {
        if (identity is null || identity.Kind is not ("msa" or "aad") || identity.Source is not
            ("microsoft-mecontrol:currentAccount" or "microsoft-whiteboard:header-userInfoCache" or "microsoft-whiteboard:header-userInfo" or "microsoft-mecontrol:currentAccount-dom")) return null;
        var email = identity.Email?.Trim();
        if (email is null || email.Length > 320 || !Regex.IsMatch(email, "^[^@\\s<>\";,]+@[^@\\s<>\";,]+\\.[^@\\s<>\";,]+$", RegexOptions.CultureInvariant)) return null;
        var tenant = Guid.TryParse(identity.TenantId, out var tenantId) ? tenantId.ToString("D") : string.Empty;
        // MeControl supplies the current memberName, never a remembered account.
        // Organizational tenants remain separate even when a UPN is reused.
        var canonical = "microsoft-account:v1\n" + identity.Kind + "\n" + (identity.Kind == "aad" ? tenant : string.Empty) + "\n" + email.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var name = identity.DisplayName?.Trim();
        if (name is null || name.Length == 0 || name.Any(char.IsControl)) name = email;
        if (name.Length > 200) name = name[..(char.IsHighSurrogate(name[199]) ? 199 : 200)];
        return new CloudAccount(key, name, email, identity.Kind, identity.Source);
    }

    public async Task<IReadOnlyList<CloudBoard>> RefreshBoardsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            _boards.Clear();
            _account = null;
            _listResponseObserved = false;
            _captureGeneration++;
            Report("正在打开微软白板列表…");
            await NavigateAsync(GalleryUrl, cancellationToken);
            await WaitForAsync("白板列表", Scripts.GalleryReady, TimeSpan.FromSeconds(45), cancellationToken);
            await EvaluateAsync<bool>(Scripts.StartGalleryScan, cancellationToken);
            var deadline = DateTime.UtcNow.AddSeconds(90);
            var stableAtBottom = 0;
            var previousCount = -1;
            try
            {
                for (var step = 0; step < 500 && DateTime.UtcNow < deadline; step++)
                {
                    var scan = await EvaluateAsync<GalleryScan>(Scripts.ScanGallery, cancellationToken);
                    if (scan is null || !scan.Ready)
                        throw new InvalidOperationException("微软白板列表在读取过程中消失。请检查页面是否退出登录或显示服务错误。");
                    foreach (var item in scan.Boards ?? [])
                        if (Guid.TryParse(item.Id, out _))
                        {
                            var modified = BoardMetadata.TryParseTimestamp(item.ModifiedText, out var parsed) ? parsed : (DateTimeOffset?)null;
                            Upsert(new CloudBoard(item.Id, item.Title, modified, item.Url, modified is null ? null : item.ModifiedSource));
                        }
                    if (scan.Bottom && !scan.Loading && _responseReads.Count == 0 &&
                        (_boards.Count > 0 || _listResponseObserved) && _boards.Count == previousCount) stableAtBottom++;
                    else stableAtBottom = 0;
                    previousCount = _boards.Count;
                    if (stableAtBottom >= 10)
                    {
                        Report(_boards.Count == 0 ? "微软白板列表为空。" : $"已读取 {_boards.Count} 个白板。");
                        return _boards.Values.ToArray();
                    }
                    if (step % 12 == 0) Report($"正在滚动读取白板列表，已找到 {_boards.Count} 个…");
                    await Task.Delay(400, cancellationToken);
                }
                throw new TimeoutException($"读取虚拟白板列表超时（目前找到 {_boards.Count} 个）。为避免漏导出，未返回不完整列表；请刷新后重试。");
            }
            finally { await TryScriptAsync(Scripts.EndGalleryScan); }
        }
        finally { _operationGate.Release(); }
    }

    public async Task<CloudCapture> CaptureAsync(CloudBoard board, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        CloudCapture? lastVerifiedCapture = null;
        for (var attempt = 0; ; attempt++)
        {
            CloudCapture capture;
            try { capture = await CaptureOnceAsync(board, destinationDirectory, cancellationToken); }
            catch (Exception exception) when (lastVerifiedCapture is not null && exception is IOException or InvalidDataException or InvalidOperationException or TimeoutException or COMException)
            { throw new CloudCaptureIncompleteException(lastVerifiedCapture, exception); }
            catch (TimeoutException) when (attempt == 0)
            {
                Report("首次等待画布或图片加载超时，正在重新打开同一白板并重试一次…");
                continue;
            }
            lastVerifiedCapture = capture;
            try { if (await CountPendingExportImagesAsync(capture.ZipPath, cancellationToken) == 0) return capture; }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or TimeoutException or COMException)
            { throw new CloudCaptureIncompleteException(capture, exception); }
            if (attempt >= 1) throw new CloudCaptureIncompleteException(capture);
            Report("官方 ZIP 仍包含图片加载占位控件，源文件已保留；正在重新打开白板并等待图片后重采一次…");
        }
    }

    private async Task<CloudCapture> CaptureOnceAsync(CloudBoard board, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (!Guid.TryParse(board.Id, out var boardGuid)) throw new ArgumentException("白板 ID 必须是有效的 UUID。", nameof(board));
        await InitializeAsync();
        var destination = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destination);
        await _operationGate.WaitAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(180));
        _activeCaptureCancellation = timeout;
        var token = timeout.Token;
        var partial = Path.Combine(destination, $".{boardGuid:D}-{Guid.NewGuid():N}.partial");
        // Every capture is an immutable source revision. A later empty/failed export
        // must never replace an earlier source ZIP that still contains the board.
        var final = Path.Combine(destination, $"{boardGuid:D}__{Guid.NewGuid():N}.zip");
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CoreWebView2DownloadOperation? download = null;
        EventHandler<object>? stateChanged = null;
        var downloadArmed = false;
        var succeeded = false;
        void OnDownload(object? sender, CoreWebView2DownloadStartingEventArgs args)
        {
            if (!downloadArmed) return;
            args.Handled = true;
            if (download is not null)
            {
                args.Cancel = true;
                return;
            }
            if (!args.ResultFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                !args.DownloadOperation.MimeType.Contains("zip", StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
                completed.TrySetException(new InvalidDataException("微软导出控件返回了非 ZIP 下载。请确认选择了 Zip (HTML+JSON)，或重新导入手动导出的微软 ZIP。"));
                return;
            }
            args.ResultFilePath = partial;
            download = args.DownloadOperation;
            stateChanged = (_, _) => CheckState();
            download.StateChanged += stateChanged;
            Report("微软正在生成并下载 ZIP…");
            CheckState();
        }
        void CheckState()
        {
            if (download?.State == CoreWebView2DownloadState.Completed) completed.TrySetResult(true);
            else if (download?.State == CoreWebView2DownloadState.Interrupted)
                completed.TrySetException(new IOException($"微软 ZIP 下载中断：{download.InterruptReason}。请检查连接后重试。"));
        }
        _captureGeneration++;
        _captureBoardId = board.Id;
        _responses.Clear();
        _resources.Clear();
        _resourceBytes = 0;
        _resourceBoardId = board.Id;
        _responseBytes = 0;
        _responseNumber = 0;
        _responseLimitReported = false;
        _webView.CoreWebView2.DownloadStarting += OnDownload;
        void CancelDownload()
        {
            try { if (download?.State == CoreWebView2DownloadState.InProgress) download.Cancel(); }
            catch (System.Runtime.InteropServices.COMException) { }
            completed.TrySetCanceled(token);
        }
        using var cancellation = token.Register(() =>
        {
            if (_webView.Dispatcher.CheckAccess()) CancelDownload();
            else if (!_webView.Dispatcher.HasShutdownStarted) _webView.Dispatcher.BeginInvoke(new Action(CancelDownload));
            else completed.TrySetCanceled(token);
        });
        try
        {
            Report($"正在打开白板：{board.Title}");
            await OpenBoardAsync(board, boardGuid, token);
            await WaitForEditorSettledAsync(token);
            await CaptureReferencedImagesAsync(token);
            await WaitForAsync("设置菜单", Scripts.OpenSettings, TimeSpan.FromSeconds(15), token);
            await WaitForAsync("Export 菜单项", Scripts.OpenExport, TimeSpan.FromSeconds(15), token);
            await WaitForAsync("Zip (HTML+JSON) 导出选项", Scripts.ZipReady, TimeSpan.FromSeconds(20), token);
            downloadArmed = true;
            if (!await EvaluateAsync<bool>(Scripts.ClickZip, token))
                throw new InvalidOperationException("微软 ZIP 导出选项已消失。页面可能已更新，请重试或使用微软白板手动导出的 ZIP。");
            await completed.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            var hasObjectMarkers = await Task.Run(() => ValidateZip(partial), token);
            // Give response reads already started on this board a bounded chance to finish.
            var reads = _responseReads.ToArray();
            if (reads.Length > 0)
            {
                try { await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(5), token); }
                catch (TimeoutException) { Report("ZIP 已完成；部分附加源响应仍未读完，未将其算作已备份。"); }
            }
            token.ThrowIfCancellationRequested();
            File.Move(partial, final);
            var metadata = _boards.GetValueOrDefault(board.Id) ?? board;
            var result = new CloudCapture(final, new Dictionary<string, byte[]>(_responses, StringComparer.Ordinal), new Dictionary<string, CapturedImageResource>(_resources, StringComparer.Ordinal), metadata.Modified, metadata.ModifiedSource);
            succeeded = true;
            Report($"ZIP 下载与导出结构已验证，附带 {result.SourceResponses.Count} 个源响应；内容支持情况将在导入后检查。");
            if (!hasObjectMarkers)
                Report("官方 ZIP 未检测到常见白板对象标记，可能是空白导出。源包已原样保存；这不能证明云端白板本身为空，导入后将显示内容诊断。");
            await TryScriptAsync(Scripts.CloseSuccess);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("微软白板导出超过 180 秒。未将截图、元数据或部分下载标记为完整；可重试或手动导出 Zip (HTML+JSON) 后导入。");
        }
        finally
        {
            downloadArmed = false;
            _activeCaptureCancellation = null;
            _captureBoardId = null;
            _captureGeneration++;
            _webView.CoreWebView2.DownloadStarting -= OnDownload;
            if (download is not null && stateChanged is not null) download.StateChanged -= stateChanged;
            if (!succeeded)
            {
                if (download?.State == CoreWebView2DownloadState.InProgress) download.Cancel();
                try { if (File.Exists(partial)) File.Delete(partial); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            _operationGate.Release();
        }
    }

    public async Task LogoutAsync()
    {
        await InitializeAsync();
        await _operationGate.WaitAsync();
        try
        {
            foreach (var window in _loginWindows.ToArray()) window.Close();
            _webView.CoreWebView2.Navigate("about:blank");
            await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            _boards.Clear();
            _account = null;
            _responses.Clear();
            _resources.Clear();
            _resourceBytes = 0;
            _resourceBoardId = null;
            _captureGeneration++;
            Report("本机专用浏览器的登录、Cookie 与缓存已清除。");
        }
        finally { _operationGate.Release(); }
    }

    private async Task OpenBoardAsync(CloudBoard board, Guid id, CancellationToken cancellationToken)
    {
        // The current app uses /me/whiteboards/{id}; older clients/catalogues used
        // ?wbid={id}. Both routes are evidenced by the referenced exporter projects.
        var candidates = new List<string>();
        if (ValidatedBoardUrl(board.Url, id.ToString("D")) is { } actual) candidates.Add(actual);
        candidates.Add($"{GalleryUrl}/{id:D}");
        candidates.Add($"https://whiteboard.cloud.microsoft/?wbid={id:D}");
        Exception? lastError = null;
        var attempt = 0;
        foreach (var url in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var duration = TimeSpan.FromSeconds(attempt++ == 0 ? 30 : 15);
                await NavigateAsync(url, cancellationToken, duration);
                await WaitForAsync("白板编辑器", Scripts.EditorReady, duration, cancellationToken);
                return;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException ||
                (exception is InvalidOperationException && !IsLoginPage(_webView.Source?.AbsoluteUri)))
            {
                lastError = exception;
                cancellationToken.ThrowIfCancellationRequested();
                Report("当前入口未打开白板，正在尝试另一个已知的微软白板入口…");
            }
        }
        throw new InvalidOperationException("已尝试当前及旧版微软白板入口，但仍未能打开编辑器。请确认账号权限及服务状态；也可导入手动导出的微软 ZIP。", lastError);
    }

    private async Task NavigateAsync(string url, CancellationToken cancellationToken, TimeSpan? duration = null)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        void Starting(object? sender, CoreWebView2NavigationStartingEventArgs args) => navigationId ??= args.NavigationId;
        void Finished(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (navigationId != args.NavigationId) return;
            if (args.IsSuccess) completion.TrySetResult(true);
            else completion.TrySetException(new IOException($"微软页面加载失败：{args.WebErrorStatus}。请检查网络及服务状态。"));
        }
        _webView.CoreWebView2.NavigationStarting += Starting;
        _webView.CoreWebView2.NavigationCompleted += Finished;
        try
        {
            _webView.CoreWebView2.Navigate(url);
            await completion.Task.WaitAsync(duration ?? TimeSpan.FromSeconds(45), cancellationToken);
        }
        finally
        {
            _webView.CoreWebView2.NavigationStarting -= Starting;
            _webView.CoreWebView2.NavigationCompleted -= Finished;
            if (!completion.Task.IsCompleted) _webView.CoreWebView2.Stop();
        }
    }

    private async Task WaitForAsync(string label, string script, TimeSpan duration, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsLoginPage(_webView.Source?.AbsoluteUri))
            {
                var started = DateTime.UtcNow;
                await WaitForSilentSignInAsync(cancellationToken);
                deadline += DateTime.UtcNow - started;
            }
            if (await EvaluateAsync<bool>(Scripts.ErrorPage, cancellationToken))
                throw new InvalidOperationException("微软白板显示了服务错误页。请确认白板仍可访问、账号有权限及服务可用。");
            if (await EvaluateAsync<bool>(script, cancellationToken)) return;
            await Task.Delay(200, cancellationToken);
        }
        throw new TimeoutException($"未找到{label}。可能尚未登录、微软服务不可用或页面控件发生变化；可以手动导出 Zip (HTML+JSON) 后导入。");
    }

    private async Task WaitForEditorSettledAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        var stableSince = DateTime.UtcNow;
        string? previous = null;
        Report("正在等待微软画布显示并结束加载…");
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsLoginPage(_webView.Source?.AbsoluteUri))
                {
                    var started = DateTime.UtcNow;
                    await WaitForSilentSignInAsync(cancellationToken);
                    deadline += DateTime.UtcNow - started;
                    previous = null;
                    stableSince = DateTime.UtcNow;
                }
                if (await EvaluateAsync<bool>(Scripts.ErrorPage, cancellationToken))
                    throw new InvalidOperationException("微软白板显示服务错误；未启动 ZIP 导出。");
                var state = await EvaluateAsync<EditorState>(Scripts.ReadEditorState, cancellationToken);
                if (state is not { Editor: true, Canvas: true, Loading: false })
                {
                    previous = null;
                    stableSince = DateTime.UtcNow;
                }
                else
                {
                    if (state.Stamp != previous)
                    {
                        previous = state.Stamp;
                        stableSince = DateTime.UtcNow;
                    }
                    // An empty surface needs a longer quiet interval; zero objects
                    // alone is never a reason to reject an otherwise ready board.
                    var required = TimeSpan.FromSeconds(state.Objects == 0 ? 8 : 4);
                    if (DateTime.UtcNow - stableSince >= required)
                    {
                        Report(state.Objects == 0
                            ? "画布已显示且加载提示已消失；当前未检测到对象。将保存官方 ZIP，空白状态仍以导出数据和内容检查为准。"
                            : "画布显示与内容变化已稳定，正在打开官方导出控件…");
                        return;
                    }
                }
                await Task.Delay(250, cancellationToken);
            }
            throw new TimeoutException("微软编辑器菜单已出现，但未能确认可见画布结束加载并保持稳定。为避免导出尚未载入的画布，未启动下载；请等待页面加载完成后重试。");
        }
        finally { await TryScriptAsync(Scripts.EndEditorWait); }
    }

    private async Task<T?> EvaluateAsync<T>(string script, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = await _webView.CoreWebView2.ExecuteScriptAsync(script).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private async Task WaitForSilentSignInAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        Report("微软正在恢复登录会话，等待静默登录重定向…");
        while (IsLoginPage(_webView.Source?.AbsoluteUri))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await EvaluateAsync<bool>(Scripts.InteractiveLogin, cancellationToken))
                    throw new LoginRequiredException("微软需要交互登录或账号选择。请在微软页面完成验证后重试；软件不会填写或读取凭据。");
            }
            catch (System.Runtime.InteropServices.COMException) { /* A redirect can replace the execution context. */ }
            if (DateTime.UtcNow >= deadline)
                throw new LoginRequiredException("微软登录重定向持续超过30秒，尚未返回白板。请检查登录页面及连接后重试。");
            await Task.Delay(250, cancellationToken);
        }
    }

    private async Task TryScriptAsync(string script)
    {
        try { if (!_disposed) await _webView.CoreWebView2.ExecuteScriptAsync(script).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException) { }
    }

    private async Task ReadImageResponseAsync(CoreWebView2WebResourceResponseView response, string source, int generation)
    {
        if (_resources.ContainsKey(source)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var stream = await response.GetContentAsync().WaitAsync(timeout.Token);
        if (stream is null) return;
        var bytes = await Task.Run(() => ResourceRecoveryService.ReadBoundedAsync(stream, ResourceRecoveryService.MaxImageBytes, timeout.Token));
        if (_disposed || generation != _captureGeneration || _captureBoardId is null) return;
        AddCapturedImage(source, bytes);
    }

    private void AddCapturedImage(string source, byte[] bytes)
    {
        if (_resources.ContainsKey(source) || _resources.Count >= 1000 || _resourceBytes + bytes.Length > 128 * 1024 * 1024) return;
        if (ResourceRecoveryService.DetectImageMime(bytes) is not { } mime) return;
        _resources[source] = new(bytes, mime);
        _resourceBytes += bytes.Length;
    }

    private async Task CaptureReferencedImagesAsync(CancellationToken cancellationToken)
    {
        var sources = await EvaluateAsync<string[]>(Scripts.ReadImageSources, cancellationToken) ?? [];
        foreach (var source in sources.Distinct(StringComparer.Ordinal).Take(1000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_resources.ContainsKey(source)) continue;
            if (await ReadLiveImageAsync(source, cancellationToken) is { } image) AddCapturedImage(source, image.Bytes);
        }
    }

    /// <summary>Reads only images actually referenced by the open, same-ID board.</summary>
    public async Task<int> ResolveAssetsAsync(BoardDocument document, CancellationToken cancellationToken = default)
    {
        VerifyAccess();
        if (_webView.CoreWebView2 is null || _resourceBoardId != document.Id ||
            ValidatedBoardUrl(_webView.Source?.AbsoluteUri, document.Id) is null) return 0;
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var recovered = ResourceRecoveryService.ApplyCapturedAssets(document, _resources);
            if (document.Scene["assets"] is JsonObject assets)
            {
                foreach (var asset in assets.Select(entry => entry.Value).OfType<JsonObject>())
                {
                    if (ResourceRecoveryService.AssetAvailable(document, asset)) continue;
                    var source = asset["source"]?.GetValue<string>();
                    if (source is null && asset["sourceHash"] is JsonValue hashValue && hashValue.TryGetValue<string>(out var hash))
                    {
                        var liveSources = await EvaluateAsync<string[]>(Scripts.ReadImageSources, cancellationToken) ?? [];
                        source = liveSources.FirstOrDefault(value => ResourceRecoveryService.SourceFingerprint(value) == hash?.ToLowerInvariant());
                    }
                    if (source is null) continue;
                    if (await ReadLiveImageAsync(source, cancellationToken) is { } image) AddCapturedImage(source, image.Bytes);
                }
                recovered += ResourceRecoveryService.ApplyCapturedAssets(document, _resources);
            }
            // Earlier official exports may contain only a loading wrapper. Match
            // its native identity to the current board; never match by position.
            foreach (var element in EnumerateSceneElements(document.Scene["elements"] as JsonArray))
            {
                if (element["kind"]?.GetValue<string>() != "image" || element["nativeId"]?.GetValue<string>() is not { } nativeId ||
                    document.Scene["diagnostics"]?["items"] is not JsonArray diagnostics) continue;
                var existingAssetId = element["assetId"]?.GetValue<string>();
                var existingMetadata = existingAssetId is null ? null : document.Scene["assets"]?[existingAssetId] as JsonObject;
                if (existingMetadata is not null && ResourceRecoveryService.AssetAvailable(document, existingMetadata)) continue;
                if (existingAssetId is null && !diagnostics.OfType<JsonObject>().Any(item => item["elementId"]?.GetValue<string>() == element["id"]?.GetValue<string>() &&
                    item["code"]?.GetValue<string>() is "IMAGE_NOT_READY" or "IMAGE_CONTENT_MISSING")) continue;
                var source = await EvaluateAsync<string>(Scripts.ImageSourceByIdentity(nativeId), cancellationToken);
                if (source is null || await ReadLiveImageAsync(source, cancellationToken) is not { } captured) continue;
                var assetId = existingAssetId ?? "recovered-" + ResourceRecoveryService.SourceFingerprint(nativeId)[..20];
                var metadata = existingMetadata ?? new JsonObject();
                metadata["source"] = source;metadata["mime"] = captured.Mime;
                if (existingMetadata is null) ((JsonObject)document.Scene["assets"]!)[assetId] = metadata;
                ResourceRecoveryService.ApplyCapturedAssets(document, new Dictionary<string, CapturedImageResource> { [source] = captured });
                if (!ResourceRecoveryService.AssetAvailable(document, metadata)) continue;
                if (existingAssetId is not null) { recovered++;continue; }
                element["assetId"] = assetId;
                var bounds = (JsonObject)element["bounds"]!;
                var x = bounds["x"]!.GetValue<double>().ToString(CultureInfo.InvariantCulture);
                var y = bounds["y"]!.GetValue<double>().ToString(CultureInfo.InvariantCulture);
                var width = bounds["width"]!.GetValue<double>().ToString(CultureInfo.InvariantCulture);
                var height = bounds["height"]!.GetValue<double>().ToString(CultureInfo.InvariantCulture);
                // Replace our synthetic loading card only; board transform,
                // bounds, layer order, groups and all other objects remain intact.
                element["html"] = $"<img src=\"wbasset:{assetId}\" alt=\"已恢复的微软图片\" style=\"position:absolute;left:{x}px;top:{y}px;width:{width}px;height:{height}px;object-fit:contain\"/>";
                element["text"] = string.Empty;
                for (var index = diagnostics.Count - 1; index >= 0; index--)
                    if (diagnostics[index] is JsonObject item && item["elementId"]?.GetValue<string>() == element["id"]?.GetValue<string>() &&
                        item["code"]?.GetValue<string>() is "IMAGE_NOT_READY" or "IMAGE_CONTENT_MISSING") diagnostics.RemoveAt(index);
                recovered++;
            }
            AssetService.RefreshDiagnostics(document);
            return recovered;
        }
        finally { _operationGate.Release(); }
    }

    private static IEnumerable<JsonObject> EnumerateSceneElements(JsonArray? elements)
    {
        foreach (var element in elements?.OfType<JsonObject>() ?? [])
        {
            yield return element;
            foreach (var child in EnumerateSceneElements(element["children"] as JsonArray)) yield return child;
        }
    }

    private async Task<CapturedImageResource?> ReadLiveImageAsync(string source, CancellationToken cancellationToken)
    {
        if (_resources.TryGetValue(source, out var existing)) return existing;
        var key = Guid.NewGuid().ToString("N");
        await EvaluateAsync<bool>(Scripts.StartImageRead(key, source, ResourceRecoveryService.MaxImageBytes), cancellationToken);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = await EvaluateAsync<ImageReadState>(Scripts.PollImageRead(key), cancellationToken);
                if (state is { Done: true })
                {
                    if (!state.Success || state.Data is null) return null;
                    var bytes = Convert.FromBase64String(state.Data);
                    var mime = ResourceRecoveryService.DetectImageMime(bytes);
                    return mime is null ? null : new(bytes, mime);
                }
                await Task.Delay(100, cancellationToken);
            }
            return null;
        }
        finally { await TryScriptAsync(Scripts.EndImageRead(key)); }
    }

    private async Task<int> CountPendingExportImagesAsync(string zipPath, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var html = archive.Entries.First(entry => entry.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(html.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var source = await reader.ReadToEndAsync(cancellationToken);
        return await EvaluateAsync<int>(Scripts.CountPendingExportImages(source), cancellationToken);
    }

    private async void OnResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        if (_disposed || !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri)) return;
        if (_captureBoardId is not null && uri.Scheme is "https" or "blob" && args.Response.StatusCode is >= 200 and < 300 &&
            args.Response.Headers.Contains("Content-Type") && args.Response.Headers.GetHeader("Content-Type").StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            var imageRead = ReadImageResponseAsync(args.Response, args.Request.Uri, _captureGeneration);
            _responseReads.Add(imageRead);
            try { await imageRead; }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or TimeoutException or OperationCanceledException or System.Runtime.InteropServices.COMException)
            { Report("一个图片响应未能附加保存；将继续检查导出包与实际图片引用。"); }
            finally { _responseReads.Remove(imageRead); }
        }
        if (!IsBoardApi(uri)) return;
        var generation = _captureGeneration;
        var captureId = _captureBoardId;
        var collection = uri.AbsolutePath.TrimEnd('/').EndsWith("/whiteboards", StringComparison.OrdinalIgnoreCase);
        var capture = captureId is not null && uri.AbsolutePath.Contains(captureId, StringComparison.OrdinalIgnoreCase);
        if (!collection && !capture) return;
        if (args.Response.StatusCode is < 200 or >= 300 || !args.Response.Headers.Contains("Content-Type") ||
            !args.Response.Headers.GetHeader("Content-Type").Contains("json", StringComparison.OrdinalIgnoreCase)) return;
        // Only the board's own detail endpoint can supply a root-level edit date.
        // Dates on comments, pictures and other subresources describe those objects.
        var boardDetail = capture && uri.AbsolutePath.TrimEnd('/').EndsWith("/" + captureId, StringComparison.OrdinalIgnoreCase);
        var read = ReadResponseAsync(args.Response, collection, capture, boardDetail, generation);
        _responseReads.Add(read);
        try { await read; }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            if (capture && generation == _captureGeneration) Report("一个附加源响应读取失败；微软 ZIP 的下载与格式仍单独验证。");
        }
        finally { _responseReads.Remove(read); }
    }

    private async Task ReadResponseAsync(CoreWebView2WebResourceResponseView response, bool collection, bool capture, bool boardDetail, int generation)
    {
        using var stream = await response.GetContentAsync();
        if (stream is null) return;
        var bytes = await Task.Run(async () =>
        {
            using var output = new MemoryStream();
            var buffer = new byte[65536];
            int count;
            while ((count = await stream.ReadAsync(buffer)) > 0)
            {
                if (output.Length + count > ResponseLimit) return null;
                await output.WriteAsync(buffer.AsMemory(0, count));
            }
            return output.ToArray();
        });
        if (_disposed) return;
        if (bytes is null)
        {
            if (capture && generation == _captureGeneration) Report("一个源响应超过 16 MiB，已跳过附加响应备份；微软 ZIP 仍单独验证。");
            return;
        }
        using var document = JsonDocument.Parse(bytes);
        if (collection && generation == _captureGeneration) ReadBoardCollection(document.RootElement);
        if (!capture || generation != _captureGeneration || _captureBoardId is null) return;
        if (boardDetail) ReadBoardDetail(document.RootElement, _captureBoardId);
        // Preserve board responses, but do not archive temporary credentials or signed
        // transport URLs embedded in a metadata response. Headers/response URLs are never saved.
        if (HasTransportSecrets(document.RootElement))
        {
            Report("一个源响应含临时授权信息，已跳过附加响应备份；微软 ZIP 仍单独保存。");
            return;
        }
        if (_responseBytes + bytes.Length > ResponseTotalLimit || _responses.Count >= 100)
        {
            if (!_responseLimitReported) Report("附加源响应备份达到 64 MiB 或 100 个响应的限制；微软 ZIP 仍单独保存。");
            _responseLimitReported = true;
            return;
        }
        _responseBytes += bytes.Length;
        _responses[$"board-response-{++_responseNumber:D4}.json"] = bytes;
    }

    private void ReadBoardCollection(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "value", "whiteboards", "boards", "items" })
                if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array)
                { ReadBoardCollection(value); return; }
            return;
        }
        if (root.ValueKind != JsonValueKind.Array) return;
        _listResponseObserved = true;
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = JsonString(item, "id") ?? JsonString(item, "whiteboardId");
            if (id is null || !Guid.TryParse(id, out _)) continue;
            var title = JsonString(item, "title") ?? JsonString(item, "name") ?? "未命名白板";
            var (modified, modifiedSource) = BoardMetadata.ReadMicrosoftLastModified(item);
            var actualUrl = JsonString(item, "webUrl") ?? JsonString(item, "url") ?? JsonString(item, "boardUrl");
            Upsert(new CloudBoard(id, title, modified, ValidatedBoardUrl(actualUrl, id) ?? $"{GalleryUrl}/{id}", modifiedSource));
        }
    }

    private void ReadBoardDetail(JsonElement root, string expectedId)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        var item = root;
        foreach (var key in new[] { "whiteboard", "board", "value" })
            if (root.TryGetProperty(key, out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
            { item = wrapped; break; }
        var id = JsonString(item, "id") ?? JsonString(item, "whiteboardId");
        if (id is not null && !id.Equals(expectedId, StringComparison.OrdinalIgnoreCase)) return;
        var (modified, modifiedSource) = BoardMetadata.ReadMicrosoftLastModified(item);
        if (modified is null) return;
        var title = JsonString(item, "title") ?? JsonString(item, "name") ?? _boards.GetValueOrDefault(expectedId)?.Title ?? "未命名白板";
        Upsert(new CloudBoard(expectedId, title, modified, ModifiedSource: modifiedSource));
    }

    private void Upsert(CloudBoard board)
    {
        if (_boards.TryGetValue(board.Id, out var prior))
        {
            // API metadata describes the board itself; a displayed/cached tile
            // must not replace its timestamp with less precise DOM metadata.
            if (prior.Modified is not null &&
                ((prior.ModifiedSource?.StartsWith("microsoft-api:", StringComparison.Ordinal) == true &&
                  board.ModifiedSource?.StartsWith("microsoft-gallery:", StringComparison.Ordinal) == true) ||
                 (prior.ModifiedSource?.StartsWith("microsoft-gallery:board-model:", StringComparison.Ordinal) == true &&
                  board.ModifiedSource?.StartsWith("microsoft-gallery:edited-caption;", StringComparison.Ordinal) == true)))
                board = board with { Modified = prior.Modified, ModifiedSource = prior.ModifiedSource };
            board = board with
            {
                Title = string.IsNullOrWhiteSpace(board.Title) || board.Title == "未命名白板" ? prior.Title : board.Title,
                Modified = board.Modified ?? prior.Modified,
                ModifiedSource = board.Modified is null ? prior.ModifiedSource : board.ModifiedSource,
                Url = board.Url ?? prior.Url
            };
        }
        _boards[board.Id] = board with { Url = ValidatedBoardUrl(board.Url, board.Id) ?? $"{GalleryUrl}/{board.Id}" };
    }

    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        // Preserve the opener relationship required by popup-based Microsoft sign-in.
        var origin = (sender as CoreWebView2)?.Source;
        if ((!IsMicrosoftPage(args.Uri) && args.Uri != "about:blank") || !IsMicrosoftPage(origin))
        { Report("该页面请求打开外部窗口。请在微软登录页面内继续，或使用原浏览器检查该链接。"); return; }
        using var deferral = args.GetDeferral();
        var popup = new WebView2();
        var window = new Window { Title = "Microsoft 登录", Width = 560, Height = 760, Content = popup, Owner = Window.GetWindow(_webView), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        _loginWindows.Add(window);
        window.Closed += (_, _) => { _loginWindows.Remove(window); popup.Dispose(); };
        try
        {
            window.Show();
            await popup.EnsureCoreWebView2Async(_environment);
            popup.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            popup.CoreWebView2.WindowCloseRequested += (_, _) => window.Close();
            args.NewWindow = popup.CoreWebView2;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        { window.Close(); Report("无法打开微软验证窗口。请关闭验证窗口后重试登录。"); }
    }

    private static bool HasTransportSecrets(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) return element.EnumerateArray().Any(HasTransportSecrets);
        if (element.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name.ToLowerInvariant();
            if (name is "token" or "accesstoken" or "access_token" or "refreshtoken" or "refresh_token" or
                "idtoken" or "id_token" or "sastoken" or "authorization" or "password" or "clientsecret" or "client_secret" or "apikey" or "api_key") return true;
            if (name.Contains("url") && property.Value.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(property.Value.GetString(), UriKind.Absolute, out var uri) &&
                uri.Query.Split('&', '?').Any(part => part.StartsWith("sig=", StringComparison.OrdinalIgnoreCase) ||
                    part.StartsWith("token=", StringComparison.OrdinalIgnoreCase) || part.StartsWith("access_token=", StringComparison.OrdinalIgnoreCase) ||
                    part.StartsWith("authkey=", StringComparison.OrdinalIgnoreCase) || part.StartsWith("signature=", StringComparison.OrdinalIgnoreCase))) return true;
            if (HasTransportSecrets(property.Value)) return true;
        }
        return false;
    }

    private static bool ValidateZip(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var page = archive.Entries.FirstOrDefault(entry => entry.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
            archive.Entries.Any(comment => comment.Name.Equals(Path.GetFileNameWithoutExtension(entry.Name) + "-comments.json", StringComparison.OrdinalIgnoreCase)));
        if (page is null) throw new InvalidDataException("下载 ZIP 缺少微软白板 HTML 与对应的 comments.json，无法确认官方导出结构。");
        if (page.Length > 256L * 1024 * 1024) throw new InvalidDataException("白板 HTML 超过 256 MiB 验证上限。请拆分白板或手动检查导出文件。");
        using var reader = new StreamReader(page.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[65536];
        var tail = string.Empty;
        var found = false;
        var hasObjectMarkers = false;
        var objectMarker = new Regex(@"<[a-z][^>]*\bdata-whiteboard-type\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            var segment = tail + new string(buffer, 0, read);
            if (segment.Contains("id=\"canvasContent\"", StringComparison.Ordinal) || segment.Contains("id='canvasContent'", StringComparison.Ordinal)) found = true;
            if (!hasObjectMarkers && objectMarker.IsMatch(segment)) hasObjectMarkers = true;
            tail = segment.Length > 100 ? segment[^100..] : segment;
        }
        if (!found) throw new InvalidDataException("ZIP 中 HTML 没有微软白板 canvasContent 结构。页面或导出格式可能发生变化。");
        var comments = archive.Entries.First(entry => entry.Name.Equals(Path.GetFileNameWithoutExtension(page.Name) + "-comments.json", StringComparison.OrdinalIgnoreCase));
        if (comments.Length > 32L * 1024 * 1024) throw new InvalidDataException("白板评论 JSON 超过 32 MiB 验证上限。");
        using var commentsStream = comments.Open();
        using var commentsDocument = JsonDocument.Parse(commentsStream);
        if (commentsDocument.RootElement.ValueKind != JsonValueKind.Object ||
            !commentsDocument.RootElement.TryGetProperty("commentThreads", out var threads) || threads.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("微软导出 ZIP 的评论 JSON 缺少 commentThreads 数组，无法确认官方导出结构。");
        return hasObjectMarkers;
    }

    private static string? JsonString(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool IsBoardApi(Uri uri) => uri.Scheme == "https" &&
        (uri.Host.EndsWith(".whiteboard.microsoft.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("whiteboard.cloud.microsoft", StringComparison.OrdinalIgnoreCase)) &&
        uri.AbsolutePath.Contains("/whiteboards", StringComparison.OrdinalIgnoreCase);
    private static string? ValidatedBoardUrl(string? value, string id)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !(uri.Host.Equals("whiteboard.cloud.microsoft", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("whiteboard.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".whiteboard.microsoft.com", StringComparison.OrdinalIgnoreCase)) ||
            uri.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return null;
        var pathId = uri.AbsolutePath.Split('/').Any(segment => segment.Equals(id, StringComparison.OrdinalIgnoreCase));
        var queryId = uri.Query.Split('&', '?').Any(part => part.Equals("wbid=" + id, StringComparison.OrdinalIgnoreCase));
        if (!pathId && !queryId) return null;
        // Gallery/queue records must not retain signed URLs or incidental query data.
        return new UriBuilder(uri) { Query = pathId ? string.Empty : "wbid=" + id, Fragment = string.Empty }.Uri.AbsoluteUri;
    }
    private static bool IsMicrosoftPage(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        (uri.Host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".microsoftonline.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".live.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("whiteboard.cloud.microsoft", StringComparison.OrdinalIgnoreCase));
    private static bool IsWhiteboardPage(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (uri.Host.Equals("whiteboard.cloud.microsoft", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("whiteboard.microsoft.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".whiteboard.microsoft.com", StringComparison.OrdinalIgnoreCase));
    private static bool IsLoginPage(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Host.EndsWith(".microsoftonline.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("login.live.com", StringComparison.OrdinalIgnoreCase));
    private void VerifyAccess()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _webView.Dispatcher.VerifyAccess();
    }
    private void Report(string message) => StatusChanged?.Invoke(message);
    public void Dispose()
    {
        if (_disposed) return;
        _webView.Dispatcher.VerifyAccess();
        _disposed = true;
        _activeCaptureCancellation?.Cancel();
        _captureGeneration++;
        if (_webView.CoreWebView2 is { } core)
        {
            core.WebResourceResponseReceived -= OnResponseReceived;
            core.NewWindowRequested -= OnNewWindowRequested;
        }
        foreach (var window in _loginWindows.ToArray()) window.Close();
        GC.SuppressFinalize(this);
    }

    private sealed record GalleryBoard(string Id, string Title, string? ModifiedText = null, string? ModifiedSource = null, string? Url = null);
    private sealed record AccountIdentity(string? DisplayName, string? Email, string? TenantId, string? Kind, string? Source);
    private sealed record GalleryScan(bool Ready, bool Bottom, bool Loading, GalleryBoard[]? Boards);
    private sealed record EditorState(bool Editor, bool Canvas, bool Loading, int Objects, string Stamp);
    private sealed record ImageReadState(bool Done, bool Success, string? Data);
    private sealed class LoginRequiredException(string message) : InvalidOperationException(message);
}

internal static class Scripts
{
    // The public Header uses ServiceContextContext.userInfoProvider.userInfo.
    // Teams stores the current profile in _userInfoCache; startup-web-msal uses
    // _userInfo. Read those exact data caches, without invoking the lazy MSAL
    // getter (which would derive identity from account/token-claim state).
    private const string AccountReader = """
        const readAccount=()=>{
          const probe={whiteboardPage:false,headerPresent:false,galleryPresent:false,getterPresent:false,configurationValid:false,reactFiberPresent:false,committedFiberFound:false,serviceContextPresent:false,userInfoProviderPresent:false,committedAncestorCount:0,contextDependencyCount:0,committedSearchNodes:0,committedHeaderModelPresent:false,headerModelValid:false,currentDomPresent:false,currentDomValid:false,source:'none'};
          const done=(identity=null,source='none')=>{probe.source=source;return {identity,probe};};
          if(location.protocol!=='https:'||!['whiteboard.cloud.microsoft','whiteboard.microsoft.com'].includes(location.hostname)&&!location.hostname.endsWith('.whiteboard.microsoft.com'))return done();
          probe.whiteboardPage=true;
          const header=document.querySelector('.meControlContainerClass'),gallery=document.querySelector('.scrollableGrid');
          probe.headerPresent=!!header;probe.galleryPresent=!!gallery;
          if(!header&&!gallery)return done();
          const own=(object,key)=>{if(!object||typeof object!=='object')return undefined;const descriptor=Object.getOwnPropertyDescriptor(object,key);return descriptor&&'value'in descriptor?descriptor.value:undefined;};
          const tenant=value=>typeof value==='string'&&/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(value)?value.toLowerCase():null;
          const normalize=(name,email,tenantId,kind,source)=>{
            if(!['msa','aad'].includes(kind)||typeof email!=='string'||email.length>320||!/^[^@\s<>";,]+@[^@\s<>";,]+\.[^@\s<>";,]+$/.test(email.trim()))return null;
            let displayName=typeof name==='string'&&name.length<=2048&&!/[\x00-\x1f\x7f]/.test(name)?name.trim():'';
            displayName=displayName||email.trim();
            if(displayName.length>200)displayName=displayName.slice(0,/[\ud800-\udbff]/.test(displayName[199])?199:200);
            return {displayName,email:email.trim(),tenantId:tenant(tenantId),kind,source};
          };
          const rootFor=start=>{let node=start;const seen=new Set();for(let hop=0;node?.return&&hop<128;hop++){if(seen.has(node))return null;seen.add(node);node=node.return;}return node&&!node.return?node:null;};
          const committedPath=(anchor,fiber)=>{
            const root=rootFor(fiber),current=root?.stateNode?.current;
            if(!current||current.return||current.stateNode?.current!==current)return null;
            // A stable DOM node may retain its mount fiber without an alternate,
            // while a return link still reaches an old ancestor or HostRoot.
            // Search only committed tree structure, identifying this exact DOM
            // node, and retain the actual parent path instead of stale returns.
            const pending=[{node:current,parents:[]}],seen=new Set();
            for(let count=0;pending.length&&count<12000;count++){
              const {node,parents}=pending.pop();if(!node||seen.has(node))continue;seen.add(node);probe.committedSearchNodes++;
              if(own(node,'stateNode')===anchor)return [node,...parents.slice().reverse()];
              if(node.sibling)pending.push({node:node.sibling,parents});
              if(node.child&&parents.length<128)pending.push({node:node.child,parents:[...parents,node]});
            }
            return null;
          };
          let model=null,modelKnown=false,modelSignedOut=false;
          const acceptContext=context=>{
            if(modelKnown)return;
            let provider;try{provider=context?.userInfoProvider;}catch{return;}
            probe.userInfoProviderPresent=probe.userInfoProviderPresent||!!provider;
            if(!provider)return;
            const teamsCache=Object.getOwnPropertyDescriptor(provider,'_userInfoCache'),webCache=Object.getOwnPropertyDescriptor(provider,'_userInfo');
            const field=teamsCache&&'value'in teamsCache?'_userInfoCache':webCache&&'value'in webCache?'_userInfo':null;
            if(!field)return;
            const user=own(provider,field);modelKnown=true;modelSignedOut=user===null;probe.committedHeaderModelPresent=true;
            const tenantId=tenant(own(user,'tenantId'));if(!tenantId)return;
            const kind=['9188040d-6c67-4c5b-b112-36a304b66dad','4925308c-f164-4d2d-bc7e-0631132e9375'].includes(tenantId)?'msa':'aad';
            model=normalize(own(user,'displayName'),own(user,'upn'),tenantId,kind,field==='_userInfo'?'microsoft-whiteboard:header-userInfo':'microsoft-whiteboard:header-userInfoCache');
          };
          // Web galleries use O365Shell instead of the desktop MeControl Header.
          // Both are inside the same named ServiceContextContext provider. Only
          // its explicit current userInfo cache is read from the gallery anchor.
          for(const anchor of [header,gallery].filter(Boolean)){
          for(const key of Object.getOwnPropertyNames(anchor).filter(name=>/^__react(?:Fiber|InternalInstance)\$/.test(name)).slice(0,4)){
            const original=anchor[key];probe.reactFiberPresent=true;
            const path=committedPath(anchor,original)||committedPath(anchor,original?.alternate);
            if(!path)continue;probe.committedFiberFound=true;
            probe.committedAncestorCount=Math.max(probe.committedAncestorCount,path.length);
            for(const fiber of path.slice(0,64)){
              let dependency=fiber.dependencies?.firstContext;
              for(let index=0;dependency&&index<16;index++,dependency=dependency.next){
                probe.contextDependencyCount++;
                if(dependency.context?.displayName==='ServiceContextContext'){probe.serviceContextPresent=true;acceptContext(dependency.memoizedValue);}
              }
              if(fiber.type?._context?.displayName==='ServiceContextContext'||fiber.type?.displayName==='ServiceContextContext'){probe.serviceContextPresent=true;acceptContext(fiber.memoizedProps?.value);}
              if(model)break;
            }
            if(model)break;
          }
          if(modelKnown)break;
          }
          probe.headerModelValid=!!model;
          const visible=node=>{if(!node||typeof node.getBoundingClientRect!=='function')return false;const rect=node.getBoundingClientRect();const style=getComputedStyle(node);return rect.width>0&&rect.height>0&&style.display!=='none'&&style.visibility!=='hidden';};
          let dom=null;
          const panels=Array.from(header?.querySelectorAll('.mectrl_currentAccount')||[]).filter(visible).slice(0,2);
          probe.currentDomPresent=panels.length>0;
          if(panels.length===1){
            const panel=panels[0],emailNode=panel.querySelector('#mectrl_currentAccount_secondary'),nameNode=panel.querySelector('#mectrl_currentAccount_primary');
            const link=panel.querySelector('a[id^="mectrl_viewAccount"][href]');
            if(emailNode&&visible(emailNode)&&link){
              try{
                const url=new URL(link.getAttribute('href'),location.href);
                if(url.protocol==='https:'&&!url.username&&!url.password&&!url.port){
                  const kind=url.hostname==='account.microsoft.com'?'msa':url.hostname==='myaccount.microsoft.com'?'aad':null;
                  const tenantId=kind==='aad'?tenant(url.searchParams.get('tid')):null;
                  if(kind==='msa'||kind==='aad'&&tenantId)dom=normalize(nameNode?.textContent,emailNode.textContent,tenantId,kind,'microsoft-mecontrol:currentAccount-dom');
                }
              }catch{}
            }
          }
          probe.currentDomValid=!!dom;
          const getOptions=window.MeControl?.WebInline?.getOptions;probe.getterPresent=typeof getOptions==='function';
          let configuration=null;
          if(probe.getterPresent){
            try{
              const current=own(getOptions(),'currentAccount');
              if(own(current,'authenticatedState')==='signedIn')configuration=normalize(own(current,'displayName'),own(current,'memberName'),own(current,'tenantId'),own(current,'type'),'microsoft-mecontrol:currentAccount');
            }catch{}
          }
          probe.configurationValid=!!configuration;
          if(model)return done(model,'header-model');
          // An authoritative signed-out/invalid current Header cache must not
          // be replaced by stale loader options from an earlier signed-in user.
          if(modelSignedOut)return done();
          if(dom)return done(dom,'current-dom');
          if(modelKnown)return done();
          return configuration?done(configuration,'configuration'):done();
        };
        """;
    internal static readonly string ReadAccount = "(() => {" + AccountReader + "return readAccount().identity;})()";
    internal static readonly string ReadAccountProbe = "(() => {" + AccountReader + "return readAccount().probe;})()";
    private const string ImageReferences = """
        const imageRoot=document.querySelector('#canvasContent');
        const roots=imageRoot?[imageRoot]:Array.from(document.querySelectorAll('.anchor[data-whiteboard-type]'));
        const effects=document.getElementById('effect-resources');if(effects&&!roots.some(root=>root.contains(effects)))roots.push(effects);
        const imageUrl=image=>{const raw=image.currentSrc||image.getAttribute('src')||image.getAttribute('href')||image.getAttribute('xlink:href');if(!raw)return null;try{const url=new URL(raw,location.href);return ['https:','blob:'].includes(url.protocol)?url.href:null;}catch{return null;}};
        const imageSources=()=>Array.from(new Set(roots.flatMap(root=>Array.from(root.querySelectorAll('img,image')).map(imageUrl).filter(Boolean))));
        """;
    internal static readonly string ReadImageSources = "(() => {" + ImageReferences + "return imageSources();})()";
    internal static string ImageSourceByIdentity(string nativeId) => "(() => {" + ImageReferences + "const id=" + JsonSerializer.Serialize(nativeId) + ";" + """
        const nodes=roots.flatMap(root=>[root,...root.querySelectorAll('[data-identity-key],[data-content-identity-key]')]).filter(node=>node.getAttribute('data-identity-key')===id||node.getAttribute('data-content-identity-key')===id);
        const sources=Array.from(new Set(nodes.flatMap(node=>[...(node.matches('img,image')?[node]:[]),...node.querySelectorAll('img,image')]).map(imageUrl).filter(Boolean)));
        return sources.length===1?sources[0]:null;})()
        """;
    internal static string StartImageRead(string key, string source, int limit) => "(() => {" + ImageReferences +
        "const key=" + JsonSerializer.Serialize(key) + ",source=" + JsonSerializer.Serialize(source) + ",limit=" + limit.ToString(CultureInfo.InvariantCulture) + ";" + """
        const states=window.__wbExporterImageReads||(window.__wbExporterImageReads=Object.create(null));
        const state={done:false,success:false,data:null,controller:new AbortController()};states[key]=state;
        if(!imageSources().includes(source)){state.done=true;return false;}
        (async()=>{try{
          const response=await fetch(source,{credentials:'include',signal:state.controller.signal});
          if(!response.ok||Number(response.headers.get('content-length')||0)>limit)throw new Error('image-unavailable');
          const reader=response.body.getReader(),chunks=[];let total=0;
          try{while(true){const {done,value}=await reader.read();if(done)break;total+=value.length;if(total>limit)throw new Error('image-too-large');chunks.push(value);}}finally{reader.cancel().catch(()=>{});}
          let binary='';for(const chunk of chunks)for(let i=0;i<chunk.length;i+=8192)binary+=String.fromCharCode(...chunk.subarray(i,i+8192));
          state.data=btoa(binary);state.success=true;
        }catch{state.success=false;}finally{state.done=true;}})();return true;})()
        """;
    internal static string PollImageRead(string key) => "(() => {const s=window.__wbExporterImageReads?.[" + JsonSerializer.Serialize(key) + "];return s?{done:s.done,success:s.success,data:s.data}:{done:true,success:false,data:null};})()";
    internal static string EndImageRead(string key) => "(() => {const states=window.__wbExporterImageReads,key=" + JsonSerializer.Serialize(key) + ";if(states?.[key]){states[key].controller.abort();delete states[key];}return true;})()";
    internal static string CountPendingExportImages(string html) => "(() => {const source=new DOMParser().parseFromString(" + JsonSerializer.Serialize(html) + ",'text/html');return Array.from(source.querySelectorAll('[data-whiteboard-type]')).filter(anchor=>['azureimage','fluidimage'].includes(anchor.getAttribute('data-whiteboard-type').toLowerCase())&&!Array.from(anchor.querySelectorAll('img,image')).some(image=>image.getAttribute('src')||image.getAttribute('href')||image.getAttribute('xlink:href'))).length;})()";

    // Each script is synchronous. Awaiting a JavaScript Promise via ExecuteScriptAsync
    // does not provide a dependable polling/cancellation boundary, so C# controls waits.
    private const string Helpers = """
        const visible = e => { if (!e) return false; const s=getComputedStyle(e),r=e.getBoundingClientRect(); if(s.display==='none'||s.visibility==='hidden'||s.opacity==='0'||r.width<=0||r.height<=0)return false;for(let p=e.parentElement;p;p=p.parentElement){const a=getComputedStyle(p);if(a.display==='none'||a.opacity==='0')return false;}return true; };
        const dismiss = () => { const got=document.querySelector('#gotItButton'); if(visible(got))got.click(); for(const d of document.querySelectorAll('.ms-Modal.is-open.ms-Dialog')) if(visible(d)&&d.querySelector('.templatePreviewButton,.useTemplateButton,.startWithBlankCanvasButton')) { const c=d.querySelector('.ms-Dialog-button--close'); if(visible(c))c.click(); } };
        """;
    internal static readonly string GalleryReady = "(() => {" + Helpers + "dismiss(); return !!document.querySelector('.scrollableGrid'); })()";
    internal static readonly string InteractiveLogin = "(() => {" + Helpers + """
        return Array.from(document.querySelectorAll('input[type="password"],input[type="email"],input[name="loginfmt"],#i0116,#i0118,#otherTile,#idSIButton9,input[type="submit"],[data-test-id="account-picker"]')).some(visible); })()
        """;
    internal static readonly string ErrorPage = "(() => {" + Helpers + "return visible(document.querySelector('#errorGoToHomeButton')); })()";
    internal static readonly string EditorReady = "(() => {" + Helpers + "dismiss(); return visible(document.querySelector('#settingsButton'))||visible(document.querySelector('#AppBarOverflowMenu')); })()";
    internal static readonly string ReadEditorState = "(() => {" + Helpers + """
        dismiss();
        const editor=[document.querySelector('#settingsButton'),document.querySelector('#AppBarOverflowMenu')].some(visible);
        // Canvas/SVG surfaces and the live ink/text classes are also used by
        // PanoramicData's DOM exporter. Do not treat toolbar icons as a canvas.
        const sufficientlyLarge=e=>{if(!visible(e))return false;const r=e.getBoundingClientRect();return r.width>=Math.min(240,innerWidth*.3)&&r.height>=Math.min(160,innerHeight*.3);};
        let surface=document.querySelector('#canvasContent');
        if(!sufficientlyLarge(surface)) surface=Array.from(document.querySelectorAll('canvas,svg')).find(sufficientlyLarge);
        const contentSelector='.anchor[data-whiteboard-type],svg.inkGroup,svg[class*="PenStroke"],svg[class*="Highlight"],.DraftEditor-editorContainer,.textBoxBackground,.stickyNote';
        if(!surface){
          const content=document.querySelector(contentSelector);
          for(let e=content?.parentElement;e&&e!==document.body;e=e.parentElement){
            const s=getComputedStyle(e);
            if(sufficientlyLarge(e)&&[s.overflow,s.overflowX,s.overflowY].some(x=>x==='hidden'||x==='clip')){surface=e;break;}
          }
        }
        if(surface&&surface.id!=='canvasContent'){
          let viewport=null;
          for(let e=surface.parentElement;e&&e!==document.body;e=e.parentElement){
            const s=getComputedStyle(e),r=e.getBoundingClientRect();
            if(sufficientlyLarge(e)&&r.width<=innerWidth+2&&r.height<=innerHeight+2&&[s.overflow,s.overflowX,s.overflowY].some(x=>x==='hidden'||x==='clip'))viewport=e;
          }
          if(viewport)surface=viewport;
        }
        const loading=Array.from(document.querySelectorAll('[role="progressbar"],.ms-Spinner,[aria-busy="true"],.ms-Shimmer-container,.ms-Shimmer-shimmerWrapper')).some(e=>{
          if(!visible(e))return false;
          // Fluent keeps a shimmer's outer container after its image is ready.
          // Prefer the actual loading wrapper when the container has one.
          if(e.matches('.ms-Shimmer-container')){const wrappers=e.querySelectorAll('.ms-Shimmer-shimmerWrapper');if(wrappers.length)return Array.from(wrappers).some(visible);}
          return true;
        });
        const images=surface?Array.from(surface.querySelectorAll('img')):[];
        const imagePending=images.some(e=>visible(e)&&!e.complete);
        let watch=window.__wbExporterEditorWait;
        if(!watch||watch.surface!==surface){
          watch?.observer.disconnect();
          watch={surface,revision:0,observer:new MutationObserver(()=>{watch.revision++;})};
          if(surface)watch.observer.observe(surface,{childList:true,subtree:true,characterData:true,attributes:true,attributeFilter:['data-whiteboard-type','data-content-blueprint-type','d','points','src','href']});
          window.__wbExporterEditorWait=watch;
        }
        // Only structural counts are returned to C#: never copy board text,
        // account data, resource URLs or arbitrary application state.
        const objects=document.querySelectorAll(contentSelector).length;
        const nodes=surface?surface.querySelectorAll('*').length:0;
        const r=surface?.getBoundingClientRect();
        return {editor,canvas:!!surface,loading:loading||imagePending,objects,
          stamp:[watch.revision,objects,nodes,images.length,r?Math.round(r.width):0,r?Math.round(r.height):0].join(':')};
        })()
        """;
    internal const string EndEditorWait = "(() => {const w=window.__wbExporterEditorWait;if(w){w.observer.disconnect();delete window.__wbExporterEditorWait;}return true;})()";
    internal static readonly string OpenSettings = "(() => {" + Helpers + "dismiss(); const e=[document.querySelector('#settingsButton'),document.querySelector('#AppBarOverflowMenu')].find(visible); if(!e)return false; e.click(); return true; })()";
    internal static readonly string OpenExport = "(() => {" + Helpers + "dismiss(); const e=Array.from(document.querySelectorAll('[data-icon-name=\"Export\"]')).map(x=>x.closest('[role=\"menuitem\"]')).find(visible); if(!e)return false; e.click(); return true; })()";
    internal static readonly string ZipReady = "(() => {" + Helpers + "dismiss(); const b=Array.from(document.querySelectorAll('.exportCallout .exportOptions button')); return visible(b.find(x=>/zip/i.test(x.textContent||x.getAttribute('aria-label')||''))||b[1]); })()";
    internal static readonly string ClickZip = "(() => {" + Helpers + "const b=Array.from(document.querySelectorAll('.exportCallout .exportOptions button')); const e=b.find(x=>/zip/i.test(x.textContent||x.getAttribute('aria-label')||''))||b[1]; if(!visible(e))return false; e.click(); return true; })()";
    internal static readonly string CloseSuccess = "(() => {" + Helpers + "for(const d of document.querySelectorAll('.ms-Modal.is-open.ms-Dialog'))if(visible(d)&&/Zip file exported|ZIP|导出|匯出/i.test(d.textContent||'')){const b=d.querySelector('button.ms-Button--default');if(visible(b)){b.click();return true;}} return false;})()";
    internal const string StartGalleryScan = """
        (() => { const g=document.querySelector('.scrollableGrid'); if(!g)return false;
        const style=document.createElement('style');style.textContent='.scrollableGrid .ms-List-page{display:flow-root}';document.head.appendChild(style);
        window.__wbExporterScan={top:g.scrollTop,style};g.scrollTop=0;return true; })()
        """;
    private const string GalleryMetadata = """
        const readGalleryModified=button=>{
          // Prefer exact machine timestamps. Some personal-account galleries
          // expose only the localized Edited caption, with minute precision.
          const iso=raw=>{
            const parts=raw.match(/^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,7})?(?:[zZ]|[+-](\d{2}):(\d{2}))$/);
            if(!parts)return false;
            const [year,month,day,hour,minute,second,offsetHour,offsetMinute]=parts.slice(1).map(value=>+(value||0));
            const leap=year%4===0&&(year%100!==0||year%400===0),days=[31,leap?29:28,31,30,31,30,31,31,30,31,30,31];
            return year>=1&&month>=1&&month<=12&&day>=1&&day<=days[month-1]&&hour<=23&&minute<=59&&second<=59&&offsetHour<=14&&offsetMinute<=59&&(offsetHour!==14||offsetMinute===0)&&Number.isFinite(Date.parse(raw));
          };
          const attributes=['data-last-modified','data-last-modified-time','data-last-modified-date-time','data-modified-time','data-modified-date-time','data-last-edited-time'];
          for(const name of attributes){
            const nodes=[button,...button.querySelectorAll('['+name+']')];
            for(const node of nodes){const raw=node.getAttribute(name)?.trim();if(raw&&iso(raw))return {modifiedText:raw,modifiedSource:'microsoft-gallery:'+name};}
          }
          for(const time of button.querySelectorAll('time[datetime]')){
            const kind=(time.getAttribute('data-timestamp-kind')||time.getAttribute('data-date-kind')||'').toLowerCase();
            const label=time.getAttribute('aria-label')||time.parentElement?.getAttribute('aria-label')||'';
            const marked=kind==='modified'||kind==='edited'||time.matches('.lastModifiedTime,.lastModifiedDateTime,.modifiedTime,.lastEditedTime')||
              !!time.closest('[data-timestamp-kind="modified"],[data-timestamp-kind="edited"],.lastModifiedTime,.lastModifiedDateTime,.modifiedTime,.lastEditedTime')||
              /last\s+(modified|edited)|最后(编辑|修改)|上次(编辑|修改)/i.test(label);
            const raw=time.getAttribute('datetime')?.trim();if(marked&&raw&&iso(raw))return {modifiedText:raw,modifiedSource:'microsoft-gallery:time[datetime]'};
          }
          // The public board-card component receives board.lastModifiedTime.
          // Read only that committed model, matched to this exact tile ID;
          // never return arbitrary component props, application state or tokens.
          for(const key of Object.getOwnPropertyNames(button).filter(name=>/^__react(?:Fiber|InternalInstance)\$/.test(name))){
            let fiber=button[key];
            const rootFor=start=>{let root=start;for(let hop=0;root?.return&&hop<128;hop++)root=root.return;return root&&!root.return?root:null;};
            let root=rootFor(fiber);
            if(root?.stateNode?.current!==root){fiber=fiber?.alternate;root=rootFor(fiber);}
            if(!root||root.stateNode?.current!==root)continue;
            for(let depth=0;fiber&&depth<32;depth++,fiber=fiber.return){
              const board=fiber.memoizedProps?.board;
              if(!board||typeof board.id!=='string'||board.id.toLowerCase()!==button.id.toLowerCase())continue;
              for(const field of ['lastModifiedTime','lastModifiedDateTime','modifiedTime','modifiedDateTime','lastEditedTime','lastEditedDateTime']){
                const raw=board[field];if(typeof raw==='string'&&iso(raw.trim()))return {modifiedText:raw.trim(),modifiedSource:'microsoft-gallery:board-model:'+field};
              }
            }
          }
          const texts=[];
          for(const node of button.querySelectorAll('.boardPreviewSubText,.captionDate,.lastModifiedTime,.lastModifiedDateTime,.modifiedTime,.lastEditedTime,[data-timestamp-kind="modified"],[data-timestamp-kind="edited"]')){
            for(const text of [node.textContent,node.getAttribute('aria-label'),node.getAttribute('title')])if(text&&text.length<=256)texts.push(text.replace(/[\u200e\u200f\u202a-\u202e\u2066-\u2069]/g,'').replace(/\s+/g,' ').trim());
          }
          const requestedLocale=new URLSearchParams(location.search).get('lng');
          const candidates=[requestedLocale,document.documentElement?.lang,...(navigator.languages||[]),navigator.language,'en-US'];
          const supported=value=>{try{return /^[a-z]{2,3}(?:-[a-z0-9]{2,8})*$/i.test(value)&&Intl.DateTimeFormat.supportedLocalesOf(value).length>0;}catch{return false;}};
          const locale=candidates.map(value=>value?.trim()).find(value=>value&&supported(value))||'en-US';
          const formatter=new Intl.DateTimeFormat(locale),zone=formatter.resolvedOptions().timeZone;
          const order=formatter.formatToParts(new Date(2020,10,22)).filter(part=>['year','month','day'].includes(part.type)).map(part=>part.type);
          const fieldsFor=(a,b,c)=>{
            if(a.length===4)return {year:+a,month:+b,day:+c};
            if(c.length!==4)return null;
            const fields={};order.forEach((name,index)=>fields[name]=+[a,b,c][index]);return fields;
          };
          for(const text of texts){
            const caption=text.match(/^(?:Edited|Last\s+edited|Last\s+modified|Modified|最后编辑(?:时间)?|最后修改(?:时间)?|上次编辑(?:时间)?|上次修改(?:时间)?|上次編輯(?:時間)?|上次修改(?:時間)?)\s*[:：]\s*(.+)$/i);
            if(!caption)continue;
            const raw=caption[1];
            if(iso(raw))return {modifiedText:raw,modifiedSource:'microsoft-gallery:edited-caption-iso'};
            const match=raw.match(/^(\d{1,4})[\/.-](\d{1,2})[\/.-](\d{1,4}),?\s+(?:(上午|下午)\s*)?(\d{1,2}):(\d{2})(?::(\d{2}))?(?:\s*(AM|PM))?$/i)||raw.match(/^(\d{4})年(\d{1,2})月(\d{1,2})日\s*(?:(上午|下午)\s*)?(\d{1,2}):(\d{2})(?::(\d{2}))?(?:\s*(AM|PM))?$/i);
            if(!match)continue;
            const fields=fieldsFor(match[1],match[2],match[3]);if(!fields)continue;
            const {year,month,day}=fields;let hour=+match[5];const minute=+match[6],second=+(match[7]||0),period=match[4]||match[8];
            if(year<1601||month<1||month>12||day<1||day>31||minute>59||second>59||hour>(period?12:23)||(period&&hour<1)||!zone)continue;
            if(period){hour%=12;if(/^(PM|下午)$/i.test(period))hour+=12;}
            const date=new Date(year,month-1,day,hour,minute,second);
            // Reject invalid dates and nonexistent clocks during DST changes.
            if(date.getFullYear()!==year||date.getMonth()!==month-1||date.getDate()!==day||date.getHours()!==hour||date.getMinutes()!==minute||date.getSeconds()!==second)continue;
            // A repeated clock at a DST fall-back has two possible instants.
            if([-120,-60,60,120].some(delta=>{const other=new Date(date.getTime()+delta*60000);return other.getFullYear()===year&&other.getMonth()===month-1&&other.getDate()===day&&other.getHours()===hour&&other.getMinutes()===minute&&other.getSeconds()===second;}))continue;
            return {modifiedText:date.toISOString(),modifiedSource:'microsoft-gallery:edited-caption;locale='+locale+';timezone='+zone+';precision='+(match[7]?'second':'minute')};
          }
          return {modifiedText:null,modifiedSource:null};
        };
        """;
    internal static readonly string ScanGallery = "(() => {" + GalleryMetadata + """
        const g=document.querySelector('.scrollableGrid'); if(!g)return {ready:false,bottom:false,loading:false,boards:[]};
        const boards=[]; for(const b of document.querySelectorAll('.boardPreviewOpenBoardButton')){
        const id=(b.id||'').trim();if(!/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(id))continue;
        const c=b.querySelector('.captionText');const t=c&&Array.from(c.childNodes).find(n=>n.nodeType===3&&(n.textContent||'').trim());
        boards.push({id,title:t?t.textContent.trim():'未命名白板',...readGalleryModified(b)});}
        const bottom=g.scrollTop+g.clientHeight>=g.scrollHeight-2;
        if(!bottom)g.scrollTop=Math.min(g.scrollHeight-g.clientHeight,g.scrollTop+Math.max(g.clientHeight*.8,240));
        return {ready:true,bottom,loading:!!g.querySelector('[role="progressbar"],.ms-Spinner'),boards};})()
        """;
    internal const string EndGalleryScan = "(() => {const x=window.__wbExporterScan,g=document.querySelector('.scrollableGrid');if(x){x.style.remove();if(g)g.scrollTop=x.top;delete window.__wbExporterScan;}return true;})()";
}
