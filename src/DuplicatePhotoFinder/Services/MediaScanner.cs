using DuplicatePhotoFinder.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DuplicatePhotoFinder.Services;

public class MediaScanner
{
    private readonly ILogger<MediaScanner> _logger;

    public MediaScanner(ILogger<MediaScanner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Phase 1: Fast scan - only path, size, date (NO image parsing, NO EXIF).
    /// Uses parallel directory traversal and DirectoryInfo.EnumerateFiles which
    /// returns FileInfo with size/date already populated (single stat per file).
    /// Full metadata (width/height/EXIF) is extracted lazily in PopulateMetadataAsync
    /// only for files that turn out to have duplicates.
    /// </summary>
    public IAsyncEnumerable<MediaFile> ScanAsync(ScanOptions options,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        return ScanInternalAsync(options, progress, ct);
    }

    private async IAsyncEnumerable<MediaFile> ScanInternalAsync(ScanOptions options,
        IProgress<string>? progress,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var results = new ConcurrentQueue<MediaFile>();
        var dirQueue = new ConcurrentQueue<DirectoryInfo>();

        var rootInfo = new DirectoryInfo(options.RootFolder);
        if (!rootInfo.Exists)
            yield break;

        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };

        // Pending dirs counter tracks: (items in queue) + (items being processed).
        // Initialize to 1 for the root, then enqueue.
        int pendingDirs = 1;
        dirQueue.Enqueue(rootInfo);

        // Throttle progress updates to avoid flooding UI thread
        long lastProgressTick = 0;
        long fileCount = 0;

        // Worker pool: each worker pulls a directory off the queue,
        // enumerates its files and subdirs, then loops.
        int workerCount = Environment.ProcessorCount * 2;
        var workerTasks = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            workerTasks[i] = Task.Run(() =>
            {
                bool done = false;
                while (!done && !ct.IsCancellationRequested)
                {
                    if (dirQueue.TryDequeue(out var dir))
                    {
                        try
                        {
                            ProcessDirectory(dir, options, enumOptions, dirQueue, results, ref fileCount, ref lastProgressTick, ref pendingDirs, progress);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to enumerate directory: {Dir}", dir.FullName);
                        }

                        // Decrement for the dir we just finished processing.
                        // If this hits 0, all work is done.
                        if (Interlocked.Decrement(ref pendingDirs) == 0)
                            done = true;
                    }
                    else
                    {
                        // Queue empty but work may still be in progress elsewhere.
                        if (Volatile.Read(ref pendingDirs) == 0)
                            done = true;
                        else
                            Thread.SpinWait(100);
                    }
                }
            }, ct);
        }

        // Drain results while workers are running. Yield as they arrive.
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (results.TryDequeue(out var file))
            {
                yield return file;
                continue;
            }

            // No results available. Check if workers are done.
            if (workerTasks.All(t => t.IsCompleted))
            {
                // Drain any remaining results
                while (results.TryDequeue(out file))
                    yield return file;
                break;
            }

            // Brief wait, then check again
            await Task.Delay(10, ct);
        }

        // Propagate worker exceptions (if any)
        await Task.WhenAll(workerTasks);
    }

    private void ProcessDirectory(
        DirectoryInfo dir,
        ScanOptions options,
        EnumerationOptions enumOptions,
        ConcurrentQueue<DirectoryInfo> dirQueue,
        ConcurrentQueue<MediaFile> results,
        ref long fileCount,
        ref long lastProgressTick,
        ref int pendingDirs,
        IProgress<string>? progress)
    {
        // Enumerate files with FileInfo (size/date already loaded - no extra stat call)
        foreach (var fi in dir.EnumerateFiles("*", enumOptions))
        {
            var ext = fi.Extension;
            if (!options.IsMediaFile(ext)) continue;

            var kind = options.ImageExtensions.Contains(ext) ? MediaKind.Image : MediaKind.Video;

            // Fast path: NO image parsing, NO EXIF reads. Just metadata we already have.
            results.Enqueue(new MediaFile
            {
                FullPath = fi.FullName,
                FileSizeBytes = fi.Length,
                DateModified = fi.LastWriteTime,
                DateTaken = null,
                WidthPixels = null,
                HeightPixels = null,
                Kind = kind
            });

            var count = Interlocked.Increment(ref fileCount);

            // Throttle progress: only update every 50 files or every 100ms
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref lastProgressTick);
            if (count % 50 == 0 || (now - last) > 100)
            {
                Interlocked.Exchange(ref lastProgressTick, now);
                progress?.Report(fi.FullName);
            }
        }

        // Enqueue subdirectories — increment BEFORE enqueue to keep counter accurate.
        foreach (var sub in dir.EnumerateDirectories("*", enumOptions))
        {
            Interlocked.Increment(ref pendingDirs);
            dirQueue.Enqueue(sub);
        }
    }

    private static bool IsHeic(string ext) =>
        ext.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".heif", StringComparison.OrdinalIgnoreCase);

    private static DateTime? ParseExifDate(string value)
    {
        if (DateTime.TryParseExact(value, "yyyy:MM:dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt))
            return dt;
        return null;
    }
}
