using System.IO;
using System.Text.Json.Nodes;
using WhiteboardExporter.App.Cloud;
using WhiteboardExporter.App.Services;
using WhiteboardExporter.Core;

namespace WhiteboardExporter.App;

/// <summary>Reads only the signed-in gallery. No board is opened, downloaded or exported.</summary>
internal static class CloudMetadataVerification
{
    public static async Task<bool> RunAsync(MainWindow window, string directory, CancellationToken cancellationToken = default)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var report = NewReport();
        try
        {
            var cloud = await window.GetCloudServiceAsync();
            var boards = await cloud.RefreshBoardsAsync(cancellationToken);
            var account = await cloud.GetAccountAsync(cancellationToken);
            report["accountIdentityKnown"] = account is not null;
            report["accountKind"] = account?.Kind;
            report["accountIdentitySource"] = account?.Source;
            report["accountKeyIsSha256"] = account?.Key.Length == 64 && account.Key.All(Uri.IsHexDigit);
            report["accountProbe"] = await cloud.GetAccountProbeAsync(cancellationToken);
            if (account is not null)
            {
                var exportAccount = new ExportAccount(account.Key, account.DisplayName, account.Email, account.Kind, account.Source).Validate();
                var accountRoot = Path.Combine(directory, "verified-account-directory");
                var accountFolder = await AccountDirectoryService.ResolveAsync(accountRoot, exportAccount, cancellationToken);
                var repeated = await AccountDirectoryService.ResolveAsync(accountRoot, exportAccount, cancellationToken);
                var restored = await AccountDirectoryService.ReadAccountAsync(accountFolder, cancellationToken);
                report["accountDirectoryRoundTrip"] = restored?.Key == exportAccount.Key && restored.Kind == exportAccount.Kind;
                report["accountDirectoryStable"] = accountFolder == repeated;
            }
            var known = boards.Count(board => board.Modified is not null);
            var api = boards.Count(board => board.Modified is not null && HasSource(board, "microsoft-api:"));
            var gallery = boards.Count(board => board.Modified is not null && HasSource(board, "microsoft-gallery:"));
            report["listedBoards"] = boards.Count;
            report["knownEditedDates"] = known;
            report["apiEditedDates"] = api;
            report["galleryEditedDates"] = gallery;
            report["galleryModelEditedDates"] = boards.Count(board => board.Modified is not null && HasSource(board, "microsoft-gallery:board-model:"));
            report["galleryCaptionEditedDates"] = boards.Count(board => board.Modified is not null && HasSource(board, "microsoft-gallery:edited-caption;"));
            report["otherEditedDates"] = known - api - gallery;
            report["unknownEditedDates"] = boards.Count - known;
            report["metadataComplete"] = known == boards.Count;
            report["success"] = true;
            await SaveAsync(directory, report, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            await WriteFailureAsync(directory, exception, CancellationToken.None);
            return false;
        }
    }

    public static async Task WriteFailureAsync(string directory, Exception exception, CancellationToken cancellationToken = default)
    {
        var report = NewReport();
        report["failedStage"] = "gallery-metadata";
        report["errorType"] = exception.GetType().Name;
        var chain = new JsonArray();
        for (Exception? current = exception; current is not null && chain.Count < 8; current = current.InnerException)
            chain.Add(new JsonObject { ["type"] = current.GetType().Name, ["hresult"] = "0x" + current.HResult.ToString("X8") });
        report["errorChain"] = chain;
        report["failureCode"] = exception.GetType().Name == "LoginRequiredException" ? "LoginInteractionRequired" :
            exception is OperationCanceledException ? "Cancelled" : exception is TimeoutException ? "GalleryTimeout" : "GalleryMetadataReadFailed";
        await SaveAsync(Path.GetFullPath(directory), report, cancellationToken);
    }

    private static bool HasSource(CloudBoard board, string prefix) => board.ModifiedSource?.StartsWith(prefix, StringComparison.Ordinal) == true;

    private static JsonObject NewReport() => new()
    {
        ["success"] = false, ["readOnlyGallery"] = true, ["openedBoards"] = 0, ["capturedBoards"] = 0
    };

    private static async Task SaveAsync(string directory, JsonObject report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "cloud-metadata-result.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, report.ToJsonString(LocalWebHost.JsonOptions), cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
