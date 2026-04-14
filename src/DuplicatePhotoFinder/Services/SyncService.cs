using Microsoft.Extensions.Logging;

namespace DuplicatePhotoFinder.Services;

public enum SyncMode
{
    OneWayCopy,  // Copy new/changed source → dest, never delete
    Mirror,      // OneWayCopy + delete extras in dest
    TwoWay       // Copy newer files in both directions, no deletions
}

public enum SyncCompareMethod
{
    ModifiedDate,    // Fast: compare last-write timestamps
    SizeAndDate      // Safer: compare size AND timestamp
}

public enum SyncActionType
{
    CopyToDestination,
    CopyToSource,
    DeleteFromDestination
}

public enum SyncActionStatus
{
    Pending,
    Completed,
    Skipped,
    Error
}

public record SyncAction(
    SyncActionType ActionType,
    string RelativePath,
    string SourcePath,
    string DestPath,
    long ByteSize)
{
    public SyncActionStatus Status { get; init; } = SyncActionStatus.Pending;
    public string? ErrorMessage { get; init; }
}

public record SyncOptions(
    string SourceFolder,
    string DestFolder,
    SyncMode Mode,
    SyncCompareMethod CompareMethod,
    bool PreviewOnly);

public record SyncSummary(
    int FilesToCopy,
    int FilesToCopyBack,
    int FilesToDelete,
    long BytesToTransfer,
    int Copied,
    int CopiedBack,
    int Deleted,
    int Skipped,
    int Errors);

public class SyncProgressUpdate
{
    public string Phase { get; init; } = string.Empty;
    public int TotalFiles { get; init; }
    public int ProcessedFiles { get; init; }
    public long BytesTransferred { get; init; }
    public string CurrentFile { get; init; } = string.Empty;
}

public class SyncService
{
    private readonly RecycleBinService _recycleBin;
    private readonly ILogger<SyncService> _logger;

    public SyncService(RecycleBinService recycleBin, ILogger<SyncService> logger)
    {
        _recycleBin = recycleBin;
        _logger = logger;
    }

    /// <summary>
    /// Scans both trees and returns the list of actions that would be performed.
    /// Does NOT execute any file operations.
    /// </summary>
    public async Task<List<SyncAction>> BuildPlanAsync(
        SyncOptions options,
        IProgress<SyncProgressUpdate>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new SyncProgressUpdate { Phase = "Scanning source folder..." });

        var sourceFiles = await Task.Run(
            () => ScanTree(options.SourceFolder), ct);

        progress?.Report(new SyncProgressUpdate { Phase = "Scanning destination folder..." });

        var destFiles = await Task.Run(
            () => ScanTree(options.DestFolder), ct);

        progress?.Report(new SyncProgressUpdate
        {
            Phase = "Comparing...",
            TotalFiles = sourceFiles.Count + destFiles.Count
        });

        var actions = await Task.Run(() => BuildActions(options, sourceFiles, destFiles), ct);

        return actions;
    }

    /// <summary>
    /// Executes a pre-built plan. Reports progress and yields each completed action.
    /// </summary>
    public async IAsyncEnumerable<SyncAction> ExecuteAsync(
        SyncOptions options,
        List<SyncAction> plan,
        IProgress<SyncProgressUpdate>? progress = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        int processed = 0;
        long bytesTransferred = 0;
        int total = plan.Count;

        foreach (var action in plan)
        {
            ct.ThrowIfCancellationRequested();

            SyncAction result;
            try
            {
                result = await Task.Run(() => ExecuteAction(action, options), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = action with { Status = SyncActionStatus.Error, ErrorMessage = ex.Message };
                _logger.LogError(ex, "Sync action failed: {ActionType} {RelativePath}", action.ActionType, action.RelativePath);
            }

            processed++;
            if (result.Status == SyncActionStatus.Completed)
                bytesTransferred += result.ByteSize;

            progress?.Report(new SyncProgressUpdate
            {
                Phase = GetPhaseLabel(action.ActionType),
                TotalFiles = total,
                ProcessedFiles = processed,
                BytesTransferred = bytesTransferred,
                CurrentFile = action.RelativePath
            });

            yield return result;
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static Dictionary<string, FileInfo> ScanTree(string root)
    {
        var result = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return result;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            result[relative] = new FileInfo(file);
        }
        return result;
    }

    private List<SyncAction> BuildActions(
        SyncOptions options,
        Dictionary<string, FileInfo> sourceFiles,
        Dictionary<string, FileInfo> destFiles)
    {
        var actions = new List<SyncAction>();

        // Files in source: copy to dest if missing or outdated
        foreach (var (rel, srcInfo) in sourceFiles)
        {
            var destPath = Path.Combine(options.DestFolder, rel);

            if (!destFiles.TryGetValue(rel, out var dstInfo))
            {
                // Dest doesn't have this file → always copy
                actions.Add(new SyncAction(SyncActionType.CopyToDestination, rel,
                    srcInfo.FullName, destPath, srcInfo.Length));
            }
            else if (NeedsUpdate(srcInfo, dstInfo, options.CompareMethod))
            {
                // Source is newer/different → overwrite dest
                actions.Add(new SyncAction(SyncActionType.CopyToDestination, rel,
                    srcInfo.FullName, destPath, srcInfo.Length));
            }
        }

        // Mirror mode: delete files in dest that don't exist in source
        if (options.Mode == SyncMode.Mirror)
        {
            foreach (var (rel, dstInfo) in destFiles)
            {
                if (!sourceFiles.ContainsKey(rel))
                {
                    var srcPath = Path.Combine(options.SourceFolder, rel);
                    actions.Add(new SyncAction(SyncActionType.DeleteFromDestination, rel,
                        srcPath, dstInfo.FullName, dstInfo.Length));
                }
            }
        }

        // Two-way: copy files in dest that are newer than source back to source
        if (options.Mode == SyncMode.TwoWay)
        {
            foreach (var (rel, dstInfo) in destFiles)
            {
                var srcPath = Path.Combine(options.SourceFolder, rel);

                if (!sourceFiles.ContainsKey(rel))
                {
                    // Dest has file that source doesn't → copy back
                    actions.Add(new SyncAction(SyncActionType.CopyToSource, rel,
                        srcPath, dstInfo.FullName, dstInfo.Length));
                }
                else if (sourceFiles.TryGetValue(rel, out var srcInfo) &&
                         NeedsUpdate(dstInfo, srcInfo, options.CompareMethod))
                {
                    // Dest is newer → copy back to source
                    actions.Add(new SyncAction(SyncActionType.CopyToSource, rel,
                        srcPath, dstInfo.FullName, dstInfo.Length));
                }
            }
        }

        return actions;
    }

    private static bool NeedsUpdate(FileInfo source, FileInfo dest, SyncCompareMethod method)
    {
        if (method == SyncCompareMethod.SizeAndDate)
            return source.LastWriteTimeUtc > dest.LastWriteTimeUtc || source.Length != dest.Length;

        // ModifiedDate only
        return source.LastWriteTimeUtc > dest.LastWriteTimeUtc;
    }

    private SyncAction ExecuteAction(SyncAction action, SyncOptions options)
    {
        _ = options; // reserved for future use (e.g. dry-run double check)

        switch (action.ActionType)
        {
            case SyncActionType.CopyToDestination:
                Directory.CreateDirectory(Path.GetDirectoryName(action.DestPath)!);
                File.Copy(action.SourcePath, action.DestPath, overwrite: true);
                _logger.LogInformation("Copied {Src} → {Dst}", action.SourcePath, action.DestPath);
                return action with { Status = SyncActionStatus.Completed };

            case SyncActionType.CopyToSource:
                Directory.CreateDirectory(Path.GetDirectoryName(action.SourcePath)!);
                File.Copy(action.DestPath, action.SourcePath, overwrite: true);
                _logger.LogInformation("Copied back {Dst} → {Src}", action.DestPath, action.SourcePath);
                return action with { Status = SyncActionStatus.Completed };

            case SyncActionType.DeleteFromDestination:
                if (File.Exists(action.DestPath))
                {
                    _recycleBin.SendToRecycleBin(action.DestPath);
                    _logger.LogInformation("Deleted (recycle bin): {Path}", action.DestPath);
                }
                return action with { Status = SyncActionStatus.Completed };

            default:
                return action with { Status = SyncActionStatus.Skipped };
        }
    }

    private static string GetPhaseLabel(SyncActionType type) => type switch
    {
        SyncActionType.CopyToDestination => "Copying to destination...",
        SyncActionType.CopyToSource => "Copying back to source...",
        SyncActionType.DeleteFromDestination => "Deleting from destination...",
        _ => "Processing..."
    };
}
