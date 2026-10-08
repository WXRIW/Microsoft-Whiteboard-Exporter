using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WhiteboardExporter.Core;

namespace WhiteboardExporter.App.Services;

public sealed class LocalWebHost(WebView2 view, string profileDirectory, bool softwareRendering = false) : IBoardNormalizer
{
    public WebView2 View { get; } = view;
    private const string Host = "appassets.example";
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private string? processFailure;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(profileDirectory);
        // A dedicated software rasterizer keeps SVG edge coverage independent
        // of GPU surface/cache changes when the WPF preview is resized.
        var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = softwareRendering ? "--disable-gpu" : "" };
        var environment = await CoreWebView2Environment.CreateAsync(null, profileDirectory, options);
        await View.EnsureCoreWebView2Async(environment);
        var core = View.CoreWebView2;
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnosticPath = Path.Combine(profileDirectory, "initialization.log");
        void Log(string message) => File.AppendAllText(diagnosticPath, DateTimeOffset.UtcNow.ToString("O") + " " + message + Environment.NewLine);
        Log($"Initialized: loaded={View.IsLoaded}, visible={View.IsVisible}, width={View.ActualWidth}, height={View.ActualHeight}");
        core.ProcessFailed += (_, e) =>
        {
            Log("Process failed: " + e.ProcessFailedKind + " " + e.Reason);
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)
            {
                processFailure = $"WebView2 子进程无法运行：{e.ProcessFailedKind} / {e.Reason}。请在普通用户桌面环境中运行，或修复 WebView2 Runtime。";
                loaded.TrySetException(new InvalidOperationException(processFailure));
            }
        };
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.SetVirtualHostNameToFolderMapping(Host, Path.Combine(AppContext.BaseDirectory, "Web"), CoreWebView2HostResourceAccessKind.DenyCors);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host != Host)
                e.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Offline content", "Content-Type: text/plain");
        };
        core.NavigationStarting += (_, e) =>
        {
            Log("Navigation: " + e.Uri);
            if (e.Uri != "about:blank" && (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Host != Host)) e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            Log("Completed: " + args.IsSuccess + " " + args.WebErrorStatus);
            if (args.IsSuccess) loaded.TrySetResult(true);
            else loaded.TrySetException(new IOException("本地预览页面加载失败：" + args.WebErrorStatus));
        }
        core.NavigationCompleted += Completed;
        try
        {
            core.Navigate($"https://{Host}/index.html");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            await WaitUntilAsync("!!window.wb", cancellationToken);
        }
        finally { core.NavigationCompleted -= Completed; }
    }

    public async Task<JsonObject> NormalizeAsync(string html, string? commentsJson, string id, string title, CancellationToken cancellationToken = default)
    {
        var input = JsonSerializer.Serialize(new { html, commentsJson, id, title });
        return (await EvaluateAsync("window.wb.normalize(" + input + ")", cancellationToken))?.AsObject()
               ?? throw new InvalidDataException("白板解析器没有返回画布数据。");
    }

    public async Task LoadAsync(BoardDocument document, CancellationToken cancellationToken = default)
    {
        var hydrated = AssetService.HydrateScene(document);
        await EvaluateAsync("window.wb.load(" + hydrated.ToJsonString() + ")", cancellationToken);
        await WaitReadyAsync(cancellationToken);
    }

    public Task<JsonNode?> EvaluateAsync(string expression, CancellationToken cancellationToken = default) => EvaluateInternalAsync(expression, cancellationToken);
    private async Task<JsonNode?> EvaluateInternalAsync(string expression, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (processFailure is not null) throw new InvalidOperationException(processFailure);
        var result = await View.CoreWebView2.ExecuteScriptAsync("(()=>{try{return {ok:true,value:(" + expression + ")};}catch(e){return {ok:false,error:String(e.stack||e)};}})()").WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        var envelope = JsonNode.Parse(result)?.AsObject() ?? throw new InvalidDataException("本地绘图页面尚未就绪。");
        if (envelope["ok"]?.GetValue<bool>() != true) throw new InvalidDataException(envelope["error"]?.GetValue<string>() ?? "本地绘图失败。");
        return envelope["value"]?.DeepClone();
    }

    public Task WaitReadyAsync(CancellationToken cancellationToken = default) => WaitUntilAsync("window.wb.isReady()", cancellationToken);
    private async Task WaitUntilAsync(string expression, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(45))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processFailure is not null) throw new InvalidOperationException(processFailure);
            var json = await View.CoreWebView2.ExecuteScriptAsync("Boolean(" + expression + ")").WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            if (json == "true") return;
            await Task.Delay(60, cancellationToken);
        }
        throw new TimeoutException("画布图片或字体加载超时。请检查缺失资源诊断。");
    }
}
