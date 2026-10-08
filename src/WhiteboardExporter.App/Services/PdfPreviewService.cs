using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace WhiteboardExporter.App.Services;

/// <summary>Browser PDF preview commands operate on the reader, not exported data.</summary>
internal static class PdfPreviewService
{
    internal static async Task<bool> EnsureFitPageAsync(CoreWebView2 core)
    {
        string? session = null;
        var deadline = Stopwatch.StartNew();
        var expectedUrl = core.Source;
        var nativeInitialized = false;
        long? firstReady = null, stableSince = null;
        try
        {
            while (deadline.ElapsedMilliseconds < 5000)
            {
                if (core.Source != expectedUrl) return false;
                var targets = JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync("Target.getTargets", "{}"))?["targetInfos"] as JsonArray;
                var currentFrame = JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"))?["frameTree"]?["frame"]?["id"]?.GetValue<string>();
                var reader = targets?.FirstOrDefault(target => target?["type"]?.GetValue<string>() == "webview" &&
                    target?["parentId"]?.GetValue<string>() == currentFrame &&
                    target?["url"]?.GetValue<string>() == "chrome-extension://mhjfbmdgcfjbbpaeojofohoefgiehjai/edge_pdf/index.html");
                if (reader is null) { await Task.Delay(100); continue; }
                var attached = JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync("Target.attachToTarget", JsonSerializer.Serialize(new { targetId = reader["targetId"]!.GetValue<string>(), flatten = true })));
                session = attached?["sessionId"]?.GetValue<string>();
                if (session is null) return false;
                while (deadline.ElapsedMilliseconds < 5000)
                {
                    if (core.Source != expectedUrl) return false;
                    var result = JsonNode.Parse(await core.CallDevToolsProtocolMethodForSessionAsync(session, "Runtime.evaluate", JsonSerializer.Serialize(new
                    {
                        expression = FitPageScript.Replace("__CONFIG__", JsonSerializer.Serialize(new { expectedUrl, initialize = !nativeInitialized }), StringComparison.Ordinal),
                        returnByValue = true
                    })));
                    var details = result?["result"]?["value"];
                    var state = details?["state"]?.GetValue<string>();
                    if (state == "page")
                    {
                        nativeInitialized = true;
                        firstReady ??= deadline.ElapsedMilliseconds;
                        if (details?["stable"]?.GetValue<bool>() == true)
                        {
                            stableSince ??= deadline.ElapsedMilliseconds;
                            // Edge can restore a previous PDF view after loading.
                            // Observe its actual zoom long enough to detect that replay.
                            if (deadline.ElapsedMilliseconds - firstReady.Value >= 1000 && deadline.ElapsedMilliseconds - stableSince.Value >= 700)
                                return true;
                        }
                        else stableSince = null;
                    }
                    else stableSince = null;
                    if (state == "unknown") return false;
                    if (state == "other-document")
                    {
                        await core.CallDevToolsProtocolMethodAsync("Target.detachFromTarget", JsonSerializer.Serialize(new { sessionId = session }));
                        session = null;
                        nativeInitialized = false;
                        firstReady = stableSince = null;
                        await Task.Delay(100);
                        break;
                    }
                    await Task.Delay(100);
                }
                if (session is not null) return false;
            }
            return false;
        }
        finally
        {
            if (session is not null)
                try { await core.CallDevToolsProtocolMethodAsync("Target.detachFromTarget", JsonSerializer.Serialize(new { sessionId = session })); } catch { }
        }
    }

    private const string FitPageScript = """
        (() => {
            const config=__CONFIG__,viewer=window.viewer,plugin=document.getElementById('plugin');
            const actual=plugin?.getAttribute('original-url')||viewer?.originalUrl_;
            if(!viewer||!actual)return {state:'loading'};
            if(actual!==config.expectedUrl)return {state:'other-document'};
            if(viewer.loadState_!=='success'||!viewer.documentDimensions_?.pageDimensions?.length)return {state:'loading'};
            const viewport=viewer.viewport;
            if(typeof viewport?.fitToPage!=='function'||typeof viewport?.setZoom!=='function'||typeof viewport?.getZoom!=='function')return {state:'unknown'};
            if(config.initialize)viewport.fitToPage();
            const size=viewport.size,page=viewer.documentDimensions_.pageDimensions[0];
            const offset=Math.max(0,Number(plugin?.getAttribute('embed-top-offset')||viewport.embedTopOffset_||0));
            const width=Number(size?.width),height=Number(size?.height)-offset-4;
            const scale=Math.min(width/Number(page.width),height/Number(page.height));
            if(!Number.isFinite(scale)||scale<=0)return {state:'unknown'};
            const factors=viewport.presetZoomFactors_;
            if(Array.isArray(factors)&&factors.length&&factors.every(factor=>Number.isFinite(factor)&&factor>0)&&scale<factors[0]) {
                if(typeof viewport.setZoomFactorRange!=='function')return {state:'unknown'};
                // Edge's normal 25% minimum cannot fit a large board in a small
                // pane. Extend only this reader's lower bound; keep its presets.
                viewport.setZoomFactorRange([Math.min(scale/2,0.01),...factors]);
            }
            const before=Number(viewport.getZoom()),different=!Number.isFinite(before)||Math.abs(before-scale)>0.00001;
            if(different)viewport.setZoom(scale);
            if(config.initialize&&typeof viewport.goToPage==='function')viewport.goToPage(0);
            const zoom=Number(viewport.getZoom());
            return {state:'page',stable:!different&&!viewport.layoutUpdateInProgress&&Math.abs(zoom-scale)<=0.00001,scale,zoom};
        })()
        """;

    internal static async Task SaveDiagnosticsAsync(CoreWebView2 core, string path)
    {
        var diagnostic = new JsonObject();
        foreach (var command in new[]
        {
            ("document", "DOM.getDocument", "{\"depth\":-1,\"pierce\":true}"),
            ("accessibility", "Accessibility.getFullAXTree", "{}"),
            ("frames", "Page.getFrameTree", "{}"),
            ("targets", "Target.getTargets", "{}")
        })
        {
            try { diagnostic[command.Item1] = JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync(command.Item2, command.Item3)); }
            catch (Exception ex) { diagnostic[command.Item1] = new JsonObject { ["error"] = ex.Message }; }
        }
        var readers = new JsonArray(); diagnostic["readers"] = readers;
        if (diagnostic["targets"]?["targetInfos"] is JsonArray targets)
            foreach (var target in targets.Where(target => target?["type"]?.GetValue<string>() == "webview" && target?["url"]?.GetValue<string>()?.StartsWith("chrome-extension://mhjfbmdgcfjbbpaeojofohoefgiehjai/", StringComparison.Ordinal) == true))
            {
                string? session = null;
                var reader = new JsonObject(); readers.Add(reader);
                try
                {
                    var attached = JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync("Target.attachToTarget", JsonSerializer.Serialize(new { targetId = target!["targetId"]!.GetValue<string>(), flatten = true })));
                    session = attached?["sessionId"]?.GetValue<string>() ?? throw new IOException("PDF target session missing.");
                    reader["document"] = JsonNode.Parse(await core.CallDevToolsProtocolMethodForSessionAsync(session, "DOM.getDocument", "{\"depth\":-1,\"pierce\":true}"));
                    reader["accessibility"] = JsonNode.Parse(await core.CallDevToolsProtocolMethodForSessionAsync(session, "Accessibility.getFullAXTree", "{}"));
                    reader["runtime"] = JsonNode.Parse(await core.CallDevToolsProtocolMethodForSessionAsync(session, "Runtime.evaluate", JsonSerializer.Serialize(new
                    {
                        expression = "JSON.stringify({viewportState:{fittingType:window.viewer?.viewport?.fittingType,zoom:window.viewer?.viewport?.zoom,getZoom:window.viewer?.viewport?.getZoom(),internalZoom:window.viewer?.viewport?.internalZoom_,zoomRaw:window.viewer?.viewport?.zoom_,embedTopOffset:window.viewer?.viewport?.embedTopOffset_,layoutPending:window.viewer?.viewport?.layoutUpdateInProgress,position:window.viewer?.viewport?.position,size:window.viewer?.viewport?.size},dimensions:window.viewer?.documentDimensions_,loadState:window.viewer?.loadState_,windowSize:{width:innerWidth,height:innerHeight},fitToView:String(window.viewer?.setFitToView),fitPageInternal:String(window.viewer?.viewport?.fitToPageInternal_),computeFit:String(window.viewer?.viewport?.computeFittingZoom_),computeFitDimensions:String(window.viewer?.viewport?.computeFittingZoomGivenDimensions_),setZoom:String(window.viewer?.viewport?.setZoom),body:document.body.innerHTML})",
                        returnByValue = true
                    })));
                }
                catch (Exception ex) { reader["error"] = ex.ToString(); }
                finally
                {
                    if (session is not null)
                        try { await core.CallDevToolsProtocolMethodAsync("Target.detachFromTarget", JsonSerializer.Serialize(new { sessionId = session })); } catch { }
                }
            }
        await File.WriteAllTextAsync(path, diagnostic.ToJsonString(LocalWebHost.JsonOptions));
    }
}
