using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhiteboardExporter.App.Cloud;
using WhiteboardExporter.Core;

namespace WhiteboardExporter.App.Services;

/// <summary>Deterministic tests: synthetic pixels and a mock HTTP transport only.</summary>
internal static class ResourceRecoveryTests
{
    internal static async Task<IReadOnlyList<string>> RunAsync(string directory)
    {
        var passed = new List<string>();
        var brush = "https://whiteboard.cloud.microsoft/blueboard/assets/rainbow.2ac5c66c9649800401ba.jpg";
        var jpeg = MakeImage(jpeg: true);
        var png = MakeImage(jpeg: false);
        var handler = new MockHandler((request, _) =>
        {
            Check(request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "Public fetch copied credentials.");
            return Task.FromResult(Reply(request, jpeg));
        });
        using var client = new HttpClient(handler);
        var service = new ResourceRecoveryService(Path.Combine(directory, "cache"), client);
        var document = MakeDocument(brush);
        Check(await service.ResolvePublicAssetsAsync(document) == 1 && document.Status == "complete" && document.Assets.Count == 1, "Public brush recovery failed.");
        passed.Add("public-brush-complete");
        var cached = MakeDocument(brush);
        Check(await service.ResolvePublicAssetsAsync(cached) == 1 && handler.Calls == 1, "Public cache made a second request.");
        passed.Add("public-brush-cache");
        var untrusted = MakeDocument("https://unrelated.invalid/private-image.jpg");
        await service.ResolvePublicAssetsAsync(untrusted);
        var signed = MakeDocument(brush + "?sig=synthetic-signature");
        await service.ResolvePublicAssetsAsync(signed);
        Check(handler.Calls == 1 && untrusted.Status == "partial" && signed.Status == "partial", "Arbitrary URLs reached the public client.");
        passed.Add("public-fetch-allowlist");
        foreach (var (name, bytes) in new[] { ("reject-html-image", Encoding.UTF8.GetBytes("<html><script>throw 1</script></html>")), ("reject-corrupt-jpeg", new byte[] {255,216,255,219,255,217}) })
        {
            using var invalidClient = new HttpClient(new MockHandler((request, _) => Task.FromResult(Reply(request, bytes))));
            var invalid = MakeDocument(brush);
            var invalidService = new ResourceRecoveryService(Path.Combine(directory, name), invalidClient);
            Check(await invalidService.ResolvePublicAssetsAsync(invalid) == 0 && invalid.Status == "partial" && invalid.Assets.Count == 0, "Invalid bytes became a complete image.");
            passed.Add(name);
        }
        using (var redirectClient = new HttpClient(new MockHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request }))))
        {
            var redirected = MakeDocument(brush);
            Check(await new ResourceRecoveryService(Path.Combine(directory, "redirect"), redirectClient).ResolvePublicAssetsAsync(redirected) == 0 && redirected.Status == "partial", "Redirect was accepted.");
            passed.Add("reject-public-redirect");
        }
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        using (var blocked = new HttpClient(new MockHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); })))
        {
            var cancelled = MakeDocument(brush);
            try { await new ResourceRecoveryService(Path.Combine(directory, "cancel"), blocked).ResolvePublicAssetsAsync(cancelled, cancellation.Token); throw new InvalidOperationException("Cancellation was ignored."); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Check(cancelled.Status == "partial" && cancelled.Assets.Count == 0 && !Directory.EnumerateFiles(Path.Combine(directory, "cancel")).Any(), "Cancellation left a complete asset or partial file.");
            passed.Add("resource-cancellation");
        }
        const string imageSource = "https://example.invalid/synthetic.png?sig=synthetic-only-token";
        var captured = new Dictionary<string, CapturedImageResource> { [imageSource] = new(png, "image/png") };
        var privateImage = MakeDocument(imageSource);
        Check(ResourceRecoveryService.ApplyCapturedAssets(privateImage, captured) == 1 && privateImage.Status == "complete", "Captured referenced bytes were not restored.");
        Check(privateImage.Scene["assets"]!["image"]!["source"] is null && privateImage.Scene["assets"]!["image"]!["sourceHash"]!.GetValue<string>() == ResourceRecoveryService.SourceFingerprint(imageSource), "Signed URL was saved to the scene.");
        passed.Add("capture-by-source-and-redact");
        var hashed = MakeDocument(null, ResourceRecoveryService.SourceFingerprint(imageSource));
        Check(ResourceRecoveryService.ApplyCapturedAssets(hashed, captured) == 1 && hashed.Status == "complete", "Source-hash recovery failed.");
        passed.Add("capture-by-source-hash");
        var mismatch = MakeDocument(null, new string('0', 64));
        Check(ResourceRecoveryService.ApplyCapturedAssets(mismatch, captured) == 0 && mismatch.Status == "partial", "Wrong image identity matched.");
        passed.Add("capture-source-mismatch");
        ResourceRecoveryService.RedactCredentialSources(signed);
        Check(signed.Scene["assets"]!["image"]!["source"] is null && signed.Status == "partial", "Missing signed image credential was retained.");
        passed.Add("unresolved-source-redaction");
        var corruptLocal = MakeDocument(brush);
        var corruptBytes = Encoding.UTF8.GetBytes("not a renderable image");
        AssetService.AddAsset(corruptLocal, "image", corruptBytes, "image/jpeg");
        var originalCorruptPath = corruptLocal.Scene["assets"]!["image"]!["path"]!.GetValue<string>();
        Check(ResourceRecoveryService.ValidateLocalAssets(corruptLocal) == 1 && corruptLocal.Status == "partial" &&
            !ResourceRecoveryService.AssetAvailable(corruptLocal, (JsonObject)corruptLocal.Scene["assets"]!["image"]!) &&
            AssetService.HydrateScene(corruptLocal)["assets"]!["image"]!["dataUrl"] is null, "Corrupt local bytes were reported as complete or hydrated.");
        Check(corruptLocal.Assets[originalCorruptPath].SequenceEqual(corruptBytes), "Validation modified the original bytes.");
        Check(await service.ResolvePublicAssetsAsync(corruptLocal) == 1 && ResourceRecoveryService.ValidateLocalAssets(corruptLocal) == 0 &&
            corruptLocal.Status == "complete" && corruptLocal.Scene["assets"]!["image"]!["invalid"] is null, "Valid recovery failed to clear the invalid flag and diagnostic.");
        passed.Add("validate-and-repair-existing-image");
        var svg = MakeDocument(null);
        AssetService.AddAsset(svg, "image", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"2\" height=\"1\"/></svg>"), "image/svg+xml");
        Check(ResourceRecoveryService.ValidateLocalAssets(svg) == 0 && svg.Status == "complete", "A valid local SVG was rejected.");
        AssetService.AddAsset(svg, "image", Encoding.UTF8.GetBytes("<!DOCTYPE svg [<!ENTITY x SYSTEM 'https://example.invalid/private'>]><svg>&x;</svg>"), "image/svg+xml");
        Check(ResourceRecoveryService.ValidateLocalAssets(svg) == 1 && svg.Status == "partial", "External-entity SVG was accepted.");
        Check(ResourceRecoveryService.DetectImageMime(Encoding.UTF8.GetBytes("<svg xmlns=\"urn:not-svg\"><rect/></svg>")) is null, "An XML document outside the SVG namespace was accepted as a rendered SVG image.");
        passed.Add("validate-svg-without-external-entities");
        var wide = MakeImage(jpeg: false, width: 8192, height: 1);
        var originalWide = wide.ToArray();
        Check(ResourceRecoveryService.DetectImageMime(wide) == "image/png" && wide.SequenceEqual(originalWide), "Bounded thumbnail validation rejected or rewrote the original full-resolution image.");
        Check(ResourceRecoveryService.DetectImageMime(MakeImage(jpeg: false, width: 50001, height: 1)) is null, "An image beyond the decoder dimension bound was accepted.");
        passed.Add("bounded-image-decode-preserves-source");
        return passed;
    }

    private static BoardDocument MakeDocument(string? source, string? hash = null)
    {
        var asset = new JsonObject { ["path"] = "assets/missing.jpg", ["mime"] = "image/jpeg" };
        if (source is not null) asset["source"] = source;
        if (hash is not null) asset["sourceHash"] = hash;
        return new BoardDocument { Scene = new JsonObject
        {
            ["schemaVersion"] = 1, ["board"] = new JsonObject { ["id"] = "synthetic", ["title"] = "合成测试" },
            ["bounds"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 2, ["height"] = 1 }, ["background"] = "#ffffff",
            ["elements"] = new JsonArray(new JsonObject { ["id"] = "image-element", ["kind"] = "image", ["assetId"] = "image" }),
            ["assets"] = new JsonObject { ["image"] = asset },
            ["diagnostics"] = new JsonObject { ["status"] = "partial", ["total"] = 1, ["supported"] = 1, ["unsupported"] = 0, ["missingAssets"] = new JsonArray("image"),
                ["items"] = new JsonArray(new JsonObject { ["code"] = "ASSET_UNAVAILABLE", ["severity"] = "warning" }) }
        } };
    }
    private static byte[] MakeImage(bool jpeg, int width = 2, int height = 1)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i + 1] = (byte)(i % 256);pixels[i + 2] = 255;pixels[i + 3] = 255; }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, checked(width * 4));
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder() : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();encoder.Save(output);return output.ToArray();
    }
    private static HttpResponseMessage Reply(HttpRequestMessage request, byte[] bytes) => new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class MockHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return send(request, cancellationToken); }
    }
}
