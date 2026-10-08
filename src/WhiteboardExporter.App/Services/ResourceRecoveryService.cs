using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Windows.Media.Imaging;
using WhiteboardExporter.App.Cloud;
using WhiteboardExporter.Core;

namespace WhiteboardExporter.App.Services;

/// <summary>Recovers bytes, never authentication headers or copied access tokens.</summary>
public sealed class ResourceRecoveryService(string cacheDirectory, HttpClient? httpClient = null)
{
    internal const int MaxImageBytes = 32 * 1024 * 1024;
    private static readonly HttpClient PublicClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(15)
    }) { Timeout = TimeSpan.FromSeconds(40) };
    private static readonly HashSet<string> PublicBrushes = new(StringComparer.Ordinal)
    {
        "https://whiteboard.cloud.microsoft/blueboard/assets/rainbow.2ac5c66c9649800401ba.jpg",
        "https://whiteboard.cloud.microsoft/blueboard/assets/galaxy.68d630734007cd489c5b.jpg"
    };
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly string _cacheDirectory = Path.GetFullPath(cacheDirectory);
    private readonly HttpClient _httpClient = httpClient ?? PublicClient;
    public event Action<string>? StatusChanged;

    public async Task<int> ResolvePublicAssetsAsync(BoardDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Scene["assets"] is not JsonObject assets) return 0;
        var recovered = 0;
        foreach (var entry in assets.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Value is not JsonObject asset || AssetAvailable(document, asset) ||
                asset["source"]?.GetValue<string>() is not { } source || !IsPublicBrushUri(source)) continue;
            try
            {
                var image = await GetPublicBrushAsync(source, cancellationToken);
                AssetService.AddAsset(document, entry.Key, image.Bytes, image.Mime);
                ClearInvalidAsset(document, entry.Key, asset);
                recovered++;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException ||
                exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { StatusChanged?.Invoke("微软公开笔刷纹理暂时无法获取；缺失诊断保留，可联网后重试。"); }
        }
        AssetService.RefreshDiagnostics(document);
        return recovered;
    }

    private async Task<CapturedImageResource> GetPublicBrushAsync(string source, CancellationToken cancellationToken)
    {
        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            var token = timeout.Token;
            Directory.CreateDirectory(_cacheDirectory);
            var path = Path.Combine(_cacheDirectory, SourceFingerprint(source) + ".jpg");
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.Length is > 0 and <= MaxImageBytes)
                {
                    var cached = await File.ReadAllBytesAsync(path, token);
                    if (DetectImageMime(cached) == "image/jpeg") return new(cached, "image/jpeg");
                }
            }
            // Only two immutable, credential-free Microsoft URLs can reach this
            // client. Redirects are rejected, and no browser cookies are copied.
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            request.Headers.Accept.ParseAdd("image/jpeg");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.RequestMessage?.RequestUri?.AbsoluteUri != source || !response.IsSuccessStatusCode)
                throw new HttpRequestException("公开笔刷响应无效或被重定向。");
            if (response.Content.Headers.ContentLength > MaxImageBytes) throw new InvalidDataException("笔刷资源超出大小限制。");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var bytes = await ReadBoundedAsync(stream, MaxImageBytes, token);
            if (DetectImageMime(bytes) != "image/jpeg") throw new InvalidDataException("公开笔刷响应不是 JPEG 图片。");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, token);
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return new(bytes, "image/jpeg");
        }
        finally { _cacheGate.Release(); }
    }

    public static int ApplyCapturedAssets(BoardDocument document, IReadOnlyDictionary<string, CapturedImageResource> resources)
    {
        if (document.Scene["assets"] is not JsonObject assets) return 0;
        var recovered = 0;
        var byHash = resources.ToDictionary(entry => SourceFingerprint(entry.Key), entry => entry, StringComparer.Ordinal);
        foreach (var entry in assets.ToArray())
        {
            if (entry.Value is not JsonObject asset || AssetAvailable(document, asset)) continue;
            var source = asset["source"]?.GetValue<string>();
            CapturedImageResource? captured = null;
            if (source is not null) resources.TryGetValue(source, out captured);
            if (captured is null && asset["sourceHash"] is JsonValue hashValue && hashValue.TryGetValue<string>(out var hash) &&
                hash is not null && byHash.TryGetValue(hash.ToLowerInvariant(), out var matched))
            { source = matched.Key; captured = matched.Value; }
            if (captured is null || source is null) continue;
            var mime = DetectImageMime(captured.Bytes);
            if (mime is null || captured.Bytes.Length > MaxImageBytes) continue;
            AssetService.AddAsset(document, entry.Key, captured.Bytes, mime);
            ClearInvalidAsset(document, entry.Key, asset);
            if (!IsPublicBrushUri(source))
            {
                asset["sourceHash"] = SourceFingerprint(source);
                asset.Remove("source");
            }
            recovered++;
        }
        AssetService.RefreshDiagnostics(document);
        return recovered;
    }

    public static void RedactCredentialSources(BoardDocument document)
    {
        if (document.Scene["assets"] is not JsonObject assets) return;
        foreach (var asset in assets.Select(entry => entry.Value).OfType<JsonObject>())
        {
            if (asset["source"]?.GetValue<string>() is not { } source || !Uri.TryCreate(source, UriKind.Absolute, out var uri)) continue;
            if (uri.UserInfo.Length == 0 && !uri.Query.Split('&', '?').Any(part =>
                new[] { "sig=", "token=", "access_token=", "authkey=", "signature=", "authorization=", "code=" }.Any(key => part.StartsWith(key, StringComparison.OrdinalIgnoreCase)))) continue;
            asset["sourceHash"] = SourceFingerprint(source);
            asset.Remove("source");
        }
    }

    public static int ValidateLocalAssets(BoardDocument document)
    {
        if (document.Scene["assets"] is not JsonObject assets || document.Scene["diagnostics"]?["items"] is not JsonArray items) return 0;
        var invalid = 0;
        foreach (var entry in assets.ToArray())
        {
            if (entry.Value is not JsonObject asset || asset["path"]?.GetValue<string>() is not { } path || !document.Assets.TryGetValue(path, out var bytes)) continue;
            var mime = DetectImageMime(bytes);
            ClearInvalidAsset(document, entry.Key, asset);
            if (mime is not null) { asset["mime"] = mime;continue; }
            asset["invalid"] = true;invalid++;
            items.Add(new JsonObject { ["assetId"] = entry.Key, ["code"] = "asset-invalid", ["severity"] = "warning", ["message"] = "当前本机图片解码器无法验证该资源（可能损坏、格式不支持或超出安全范围）；原文件已保留，显示占位。" });
        }
        AssetService.RefreshDiagnostics(document);
        return invalid;
    }
    private static void ClearInvalidAsset(BoardDocument document, string assetId, JsonObject asset)
    {
        asset.Remove("invalid");
        if (document.Scene["diagnostics"]?["items"] is not JsonArray items) return;
        for (var i = items.Count - 1; i >= 0; i--)
            if (items[i] is JsonObject item && item["code"]?.GetValue<string>() == "asset-invalid" && item["assetId"]?.GetValue<string>() == assetId) items.RemoveAt(i);
    }
    internal static bool AssetAvailable(BoardDocument document, JsonObject asset) =>
        !(asset["invalid"] is JsonValue flag && flag.TryGetValue<bool>(out var invalid) && invalid) && asset["path"]?.GetValue<string>() is { } path && document.Assets.ContainsKey(path);
    public static bool IsPublicBrushUri(string value) => PublicBrushes.Contains(value);
    public static string SourceFingerprint(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("图片资源超出大小限制。");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        return output.ToArray();
    }

    internal static string? DetectImageMime(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.Length > MaxImageBytes) return null;
        string? mime = null;
        if (span.Length >= 8 && span[..8].SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) mime = "image/png";
        else if (span.Length >= 4 && span[0] == 255 && span[1] == 216 && span[2] == 255 && span[^2] == 255 && span[^1] == 217) mime = "image/jpeg";
        else if (span.Length >= 6 && (span[..6].SequenceEqual("GIF87a"u8) || span[..6].SequenceEqual("GIF89a"u8))) mime = "image/gif";
        else if (span.Length >= 12 && span[..4].SequenceEqual("RIFF"u8) && span.Slice(8,4).SequenceEqual("WEBP"u8)) mime = "image/webp";
        else if (span.Length >= 14 && span[..2].SequenceEqual("BM"u8)) mime = "image/bmp";
        if (mime is not null)
        {
            try
            {
                using var input = new MemoryStream(bytes, writable: false);
                var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (decoder.Frames.Count is < 1 or > 256) return null;
                var frame = decoder.Frames[0];
                if (frame.PixelWidth is <= 0 or > 50000 || frame.PixelHeight is <= 0 or > 50000) return null;
                // Decode a bounded first-frame thumbnail to avoid WIC's full
                // native-frame cache. Preserve original bytes/resolution.
                input.Position = 0;
                var thumbnail = new BitmapImage();thumbnail.BeginInit();
                thumbnail.CacheOption = BitmapCacheOption.OnLoad;
                thumbnail.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (Math.Max(frame.PixelWidth, frame.PixelHeight) > 1024)
                {
                    if (frame.PixelWidth >= frame.PixelHeight) thumbnail.DecodePixelWidth = 1024;
                    else thumbnail.DecodePixelHeight = 1024;
                }
                thumbnail.StreamSource = input;thumbnail.EndInit();
                var stride = checked((thumbnail.PixelWidth * thumbnail.Format.BitsPerPixel + 7) / 8);
                thumbnail.CopyPixels(new byte[checked(stride * thumbnail.PixelHeight)], stride, 0);
                return mime;
            }
            catch (Exception exception) when (exception is NotSupportedException or FileFormatException or ArgumentException or IOException or OverflowException)
            { return null; }
        }
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxImageBytes });
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "svg" || reader.NamespaceURI != "http://www.w3.org/2000/svg") return null;
            while (reader.Read()) { }
            return "image/svg+xml";
        }
        catch (XmlException) { return null; }
    }
}
