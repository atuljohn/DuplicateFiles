using DuplicatePhotoFinder.Models;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace DuplicatePhotoFinder.Services;

public record ScanProgressUpdate(
    string Phase,
    int FilesProcessed,
    int TotalFiles,
    int GroupsFound,
    string CurrentFile);

public class DuplicateDetector
{
    private readonly MediaScanner _scanner;
    private readonly CryptographicHashService _cryptoHash;
    private readonly PerceptualHashService _perceptualHash;
    private readonly VideoFingerprintService _videoFingerprint;
    private readonly ILogger<DuplicateDetector> _logger;

    public DuplicateDetector(
        MediaScanner scanner,
        CryptographicHashService cryptoHash,
        PerceptualHashService perceptualHash,
        VideoFingerprintService videoFingerprint,
        ILogger<DuplicateDetector> logger)
    {
        _scanner = scanner;
        _cryptoHash = cryptoHash;
        _perceptualHash = perceptualHash;
        _videoFingerprint = videoFingerprint;
        _logger = logger;
    }

    public async IAsyncEnumerable<DuplicateGroup> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgressUpdate>? progress = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Phase 1: Collect all files
        progress?.Report(new ScanProgressUpdate("Scanning files...", 0, 0, 0, ""));
        var allFiles = new List<MediaFile>();

        var scanProgress = new Progress<string>(p => progress?.Report(
            new ScanProgressUpdate("Scanning files...", allFiles.Count, 0, 0, p)));

        await foreach (var file in _scanner.ScanAsync(options, scanProgress, ct))
        {
            allFiles.Add(file);
        }

        int total = allFiles.Count;
        _logger.LogInformation("Found {Count} media files", total);

        // Group by file size first (eliminates most non-duplicates)
        var sizeGroups = allFiles.GroupBy(f => f.FileSizeBytes)
            .Where(g => g.Count() > 1)
            .ToList();

        var processed = 0;
        var groupsFound = 0;

        // Phase 2: Exact hash matching
        if (options.DetectExact)
        {
            progress?.Report(new ScanProgressUpdate("Computing exact hashes...", 0, total, groupsFound, ""));

            var semaphore = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount - 1));
            var hashTasks = new List<Task>();

            foreach (var group in sizeGroups)
            {
                foreach (var file in group)
                {
                    ct.ThrowIfCancellationRequested();
                    await semaphore.WaitAsync(ct);
                    hashTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            file.ExactHash = await _cryptoHash.ComputeSha256Async(file.FullPath, ct);
                        }
                        finally
                        {
                            semaphore.Release();
                            var p = Interlocked.Increment(ref processed);
                            progress?.Report(new ScanProgressUpdate("Computing exact hashes...",
                                p, total, groupsFound, file.FullPath));
                        }
                    }, ct));
                }
            }

            await Task.WhenAll(hashTasks);

            var exactGroups = sizeGroups
                .SelectMany(g => g)
                .Where(f => f.ExactHash != null)
                .GroupBy(f => f.ExactHash!)
                .Where(g => g.Count() > 1);

            foreach (var grp in exactGroups)
            {
                groupsFound++;
                yield return new DuplicateGroup
                {
                    MatchKind = DuplicateMatchKind.Exact,
                    Files = grp.ToList()
                };
            }
        }

        // Phase 3: Perceptual hash for images
        if (options.DetectPerceptual)
        {
            progress?.Report(new ScanProgressUpdate("Computing perceptual hashes...", processed, total, groupsFound, ""));

            var imageFiles = allFiles
                .Where(f => f.Kind == MediaKind.Image && f.ExactHash == null)
                .ToList();

            var semaphore = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount - 1));
            var hashTasks = new List<Task>();

            foreach (var file in imageFiles)
            {
                ct.ThrowIfCancellationRequested();
                await semaphore.WaitAsync(ct);
                hashTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        file.PerceptualHash = await _perceptualHash.ComputeAsync(file.FullPath, ct);
                    }
                    finally
                    {
                        semaphore.Release();
                        var p = Interlocked.Increment(ref processed);
                        progress?.Report(new ScanProgressUpdate("Computing perceptual hashes...",
                            p, total, groupsFound, file.FullPath));
                    }
                }, ct));
            }

            await Task.WhenAll(hashTasks);

            // Group by perceptual hash similarity using union-find approach
            var hashedImages = imageFiles.Where(f => f.PerceptualHash.HasValue).ToList();
            var groups = FindPerceptualGroups(hashedImages, options.PerceptualThreshold);

            foreach (var grp in groups)
            {
                groupsFound++;
                yield return new DuplicateGroup
                {
                    MatchKind = DuplicateMatchKind.Perceptual,
                    Files = grp
                };
            }
        }

        // Phase 4: Video fingerprinting
        if (options.DetectVideo)
        {
            progress?.Report(new ScanProgressUpdate("Computing video fingerprints...", processed, total, groupsFound, ""));

            var videoFiles = allFiles
                .Where(f => f.Kind == MediaKind.Video && f.ExactHash == null)
                .ToList();

            foreach (var file in videoFiles)
            {
                ct.ThrowIfCancellationRequested();
                file.VideoFingerprint = await _videoFingerprint.ComputeFingerprintAsync(
                    file.FullPath, options.VideoFrameCount, ct);
                var p = Interlocked.Increment(ref processed);
                progress?.Report(new ScanProgressUpdate("Computing video fingerprints...",
                    p, total, groupsFound, file.FullPath));
            }

            var fingerprinted = videoFiles.Where(f => f.VideoFingerprint != null).ToList();
            var videoGroups = FindVideoGroups(fingerprinted, options.PerceptualThreshold);

            foreach (var grp in videoGroups)
            {
                groupsFound++;
                yield return new DuplicateGroup
                {
                    MatchKind = DuplicateMatchKind.Video,
                    Files = grp
                };
            }
        }

        progress?.Report(new ScanProgressUpdate("Done", total, total, groupsFound, ""));
    }

    private List<List<MediaFile>> FindPerceptualGroups(List<MediaFile> files, int threshold)
    {
        var parent = Enumerable.Range(0, files.Count).ToArray();

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        void Union(int a, int b) { parent[Find(a)] = Find(b); }

        for (int i = 0; i < files.Count; i++)
            for (int j = i + 1; j < files.Count; j++)
                if (PerceptualHashService.HammingDistance(files[i].PerceptualHash!.Value, files[j].PerceptualHash!.Value) <= threshold)
                    Union(i, j);

        return files
            .Select((f, i) => (f, root: Find(i)))
            .GroupBy(x => x.root)
            .Where(g => g.Count() > 1)
            .Select(g => g.Select(x => x.f).ToList())
            .ToList();
    }

    private List<List<MediaFile>> FindVideoGroups(List<MediaFile> files, int threshold)
    {
        var parent = Enumerable.Range(0, files.Count).ToArray();

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        void Union(int a, int b) { parent[Find(a)] = Find(b); }

        for (int i = 0; i < files.Count; i++)
            for (int j = i + 1; j < files.Count; j++)
                if (VideoFingerprintService.FingerprintDistance(files[i].VideoFingerprint!, files[j].VideoFingerprint!) <= threshold)
                    Union(i, j);

        return files
            .Select((f, i) => (f, root: Find(i)))
            .GroupBy(x => x.root)
            .Where(g => g.Count() > 1)
            .Select(g => g.Select(x => x.f).ToList())
            .ToList();
    }
}
