using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace WhiteboardExporter.Core;

internal static class StorageSafety
{
    internal const long MaxFileBytes = 128L * 1024 * 1024;
    internal const long MaxArchiveBytes = 512L * 1024 * 1024;
    internal const int MaxEntries = 20_000;

    internal static string SafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024)
            throw new InvalidDataException("文件路径为空或过长。");
        var path = value.Replace('\\', '/');
        if (path.StartsWith('/') || path.Contains(':') || path.Contains('\0') || path.Any(char.IsControl))
            throw new InvalidDataException($"不安全的相对路径：{value}");
        var parts = path.Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.')))
            throw new InvalidDataException($"不安全的相对路径：{value}");
        foreach (var part in parts)
        {
            if (part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
                throw new InvalidDataException($"无效的文件名：{value}");
            var stem = part.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9'))
                throw new InvalidDataException($"保留的 Windows 文件名：{value}");
        }
        return path;
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static string ResolveUnder(string directory, string relative)
    {
        relative = SafeRelativePath(relative);
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("文件路径超出数据目录。");
        // JSON imports never follow junctions or symlinks into another directory.
        for (var current = new FileInfo(result) as FileSystemInfo; current is not null && !current.FullName.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("不允许资源路径通过符号链接或目录联接。");
            current = current is FileInfo fi ? fi.Directory : (current as DirectoryInfo)?.Parent;
        }
        return result;
    }

    internal static async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken, long limit = MaxFileBytes)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadBoundedAsync(stream, limit, cancellationToken);
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, long limit, CancellationToken cancellationToken)
    {
        if (stream.CanSeek && stream.Length > limit)
            throw new InvalidDataException($"文件超过 {limit / 1024 / 1024} MiB 限制。");
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (memory.Length + count > limit) throw new InvalidDataException("文件解压后超过大小限制。");
            await memory.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        return memory.ToArray();
    }

    internal static async Task<Dictionary<string, byte[]>> ReadZipAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(bytes, false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxEntries) throw new InvalidDataException("压缩包文件数量超过限制。");
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var name = SafeRelativePath(isDirectory ? entry.FullName.TrimEnd('/', '\\') : entry.FullName);
            if (!names.Add(name)) throw new InvalidDataException($"压缩包存在重复或大小写冲突的路径：{name}");
            if (isDirectory) continue;
            if (entry.Length > MaxFileBytes || (total += entry.Length) > MaxArchiveBytes)
                throw new InvalidDataException("压缩包解压后超过大小限制。");
            if (entry.Length > 1024 * 1024 && entry.Length > Math.Max(1, entry.CompressedLength) * 1000)
                throw new InvalidDataException("压缩包包含异常高压缩比的数据。");
            await using var entryStream = entry.Open();
            var data = await ReadBoundedAsync(entryStream, MaxFileBytes, cancellationToken);
            if (data.LongLength != entry.Length) throw new InvalidDataException("压缩包条目的长度不一致。");
            result.Add(name, data);
        }
        // File/directory aliases are ambiguous on Windows even when no extraction occurs.
        foreach (var name in result.Keys)
        {
            var slash = name.LastIndexOf('/');
            while (slash >= 0)
            {
                if (result.ContainsKey(name[..slash])) throw new InvalidDataException("压缩包包含文件与目录路径冲突。");
                slash = name.LastIndexOf('/', slash - 1);
            }
        }
        return result;
    }

    internal static JsonObject ParseObject(byte[] bytes, string name)
    {
        try
        {
            using (var parsed = System.Text.Json.JsonDocument.Parse(bytes, new() { MaxDepth = 64 }))
                ValidateUniqueJsonProperties(parsed.RootElement);
            return JsonNode.Parse(bytes, documentOptions: new() { MaxDepth = 64 }) as JsonObject
                ?? throw new InvalidDataException($"{name} 必须为 JSON 对象。");
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new InvalidDataException($"{name} 不是有效 JSON。", exception);
        }
    }

    private static void ValidateUniqueJsonProperties(System.Text.Json.JsonElement node)
    {
        if (node.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("JSON 存在重复的属性名。");
                ValidateUniqueJsonProperties(property.Value);
            }
        }
        else if (node.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var element in node.EnumerateArray()) ValidateUniqueJsonProperties(element);
    }

    internal static string Utf8(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes);
}
