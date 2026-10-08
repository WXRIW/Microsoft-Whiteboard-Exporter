using System.IO;

namespace WhiteboardExporter.App.Services;

/// <summary>
/// Native PDF/WIC writers use a short scratch path. A managed copy followed by
/// a destination-directory rename keeps long paths and cross-volume output atomic.
/// </summary>
internal sealed class NativeExportFile : IDisposable
{
    public string Path { get; }

    public NativeExportFile(string extension)
    {
        if (extension is not (".pdf" or ".png" or ".jpg")) throw new ArgumentException("无效的原生导出格式。", nameof(extension));
        var name = "wbex-" + Guid.NewGuid().ToString("N") + extension;
        var candidates = new[]
        {
            System.IO.Path.GetTempPath(),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp")
        }.Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in candidates)
        {
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, name));
            // Leave room for native APIs' MAX_PATH accounting and terminator.
            if (path.Length >= 240) continue;
            Directory.CreateDirectory(directory);
            if (File.Exists(path) || Directory.Exists(path)) continue;
            Path = path;
            return;
        }
        throw new IOException("无法找到短临时目录；请将系统 TEMP 目录设置为较短的本机路径。");
    }

    public async Task CommitAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        targetPath = System.IO.Path.GetFullPath(targetPath);
        var directory = System.IO.Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);
        // Only .NET sees this path. Do not append to a potentially long filename.
        var staging = System.IO.Path.Combine(directory, ".wbex-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var source = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Staging and destination are on the same volume, even when scratch
            // is elsewhere. A failed or cancelled copy leaves the old file intact.
            File.Move(staging, targetPath, true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    public void Dispose()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}
