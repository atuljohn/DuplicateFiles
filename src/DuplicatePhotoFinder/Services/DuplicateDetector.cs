using DuplicatePhotoFinder.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
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

        // Phase 2: Exact hash matching with increased parallelism
        if (options.DetectExact)
        {
            progress?.Report(new ScanProgressUpdate("Computing exact hashes...", 0, total, groupsFound, ""));

            // Increased to ProcessorCount * 2 for better CPU utilization during I/O waits
            var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2);
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

        // Phase 3: Perceptual hash for images with parallelism
        if (options.DetectPerceptual)
        {
            progress?.Report(new ScanProgressUpdate("Computing perceptual hashes...", processed, total, groupsFound, ""));

            var imageFiles = allFiles
                .Where(f => f.Kind == MediaKind.Image && f.ExactHash == null)
                .ToList();

            // Perceptual hashing is CPU-bound (JPEG decode) but ImageSharp pools
            // pixel buffers per concurrent op, so 4× oversubscription was retaining
            // multi-GB of pooled memory on libraries with large source images.
            // 2× keeps cores busy without blowing the pool.
            var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2);
            var hashTasks = new List<Task>();

            foreach (var file in imageFiles)
            {
                ct.ThrowIfCancellationRequested();
                await semaphore.WaitAsync(ct);
                hashTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var (hash, _) = await _perceptualHash.ComputeWithHistogramAsync(file.FullPath, ct);
                        file.PerceptualHash = hash;
                        // Histogram intentionally dropped: 256 floats × every file = wasted memory,
                        // and Pass 2 MSE verification is what we actually use for confirmation.
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

            // Release pooled image buffers retained from the perceptual hash decodes.
            SixLabors.ImageSharp.Configuration.Default.MemoryAllocator.ReleaseRetainedResources();

            // All perceptual groups start as Unconfirmed — Pass 2 MSE check is always required.
            // Empirical analysis showed even Hamming=0 can be a false positive on real photos.
            var hashedImages = imageFiles.Where(f => f.PerceptualHash.HasValue).ToList();
            var (highConfGroups, uncertainGroups) = FindPerceptualGroupsParallel(
                hashedImages, options.PerceptualThreshold, options.PerceptualHighConfidenceThreshold);

            foreach (var grp in highConfGroups.Concat(uncertainGroups))
            {
                groupsFound++;
                yield return new DuplicateGroup
                {
                    MatchKind = DuplicateMatchKind.Perceptual,
                    Confidence = MatchConfidence.Unconfirmed,
                    MinHammingDistance = grp.minDist,
                    Files = grp.files
                };
            }
        }

        // Phase 4: Video fingerprinting with parallelism (was fully sequential before)
        if (options.DetectVideo)
        {
            progress?.Report(new ScanProgressUpdate("Computing video fingerprints...", processed, total, groupsFound, ""));

            var videoFiles = allFiles
                .Where(f => f.Kind == MediaKind.Video && f.ExactHash == null)
                .ToList();

            // Apply semaphore to video processing (was sequential before)
            var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2);
            var fpTasks = new List<Task>();

            foreach (var file in videoFiles)
            {
                ct.ThrowIfCancellationRequested();
                await semaphore.WaitAsync(ct);
                fpTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        file.VideoFingerprint = await _videoFingerprint.ComputeFingerprintAsync(
                            file.FullPath, options.VideoFrameCount, ct);
                    }
                    finally
                    {
                        semaphore.Release();
                        var p = Interlocked.Increment(ref processed);
                        progress?.Report(new ScanProgressUpdate("Computing video fingerprints...",
                            p, total, groupsFound, file.FullPath));
                    }
                }, ct));
            }

            await Task.WhenAll(fpTasks);

            // Parallelize video grouping
            var fingerprinted = videoFiles.Where(f => f.VideoFingerprint != null).ToList();
            var videoGroups = FindVideoGroupsParallel(fingerprinted, options.PerceptualThreshold);

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

    private (List<(List<MediaFile> files, int minDist)> highConf, List<(List<MediaFile> files, int minDist)> uncertain)
        FindPerceptualGroupsParallel(List<MediaFile> files, int threshold, int highConfThreshold)
    {
        if (files.Count == 0) return (new(), new());

        var parent = Enumerable.Range(0, files.Count).ToArray();
        var parentLock = new object();
        // Track min Hamming distance per root group
        var minDist = Enumerable.Repeat(int.MaxValue, files.Count).ToArray();

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        void Union(int a, int b, int dist)
        {
            lock (parentLock)
            {
                int ra = Find(a), rb = Find(b);
                parent[ra] = rb;
                minDist[rb] = Math.Min(minDist[rb], Math.Min(minDist[ra], dist));
            }
        }

        // Parallelize O(n²) comparisons
        Parallel.For(0, files.Count, i =>
        {
            for (int j = i + 1; j < files.Count; j++)
            {
                var d = PerceptualHashService.HammingDistance(
                    files[i].PerceptualHash!.Value, files[j].PerceptualHash!.Value);
                if (d <= threshold)
                    Union(i, j, d);
            }
        });

        var grouped = files
            .Select((f, i) => (f, root: Find(i), dist: minDist[Find(i)]))
            .GroupBy(x => x.root)
            .Where(g => g.Count() > 1)
            .Select(g => (files: g.Select(x => x.f).ToList(), minDist: g.First().dist))
            .ToList();

        var highConf = grouped.Where(g => g.minDist <= highConfThreshold).ToList();
        var uncertain = grouped.Where(g => g.minDist > highConfThreshold).ToList();
        return (highConf, uncertain);
    }

    private List<List<MediaFile>> FindVideoGroupsParallel(List<MediaFile> files, int threshold)
    {
        if (files.Count == 0) return new();

        var parent = Enumerable.Range(0, files.Count).ToArray();
        var parentLock = new object();

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        void Union(int a, int b)
        {
            lock (parentLock)
            {
                parent[Find(a)] = Find(b);
            }
        }

        // Pre-parse fingerprints once instead of O(N²) string splits
        var parsedFingerprints = files.Select(f =>
            f.VideoFingerprint!.Split(',').Select(ulong.Parse).ToArray()).ToList();

        // Parallelize O(n²) comparisons
        Parallel.For(0, files.Count, i =>
        {
            for (int j = i + 1; j < files.Count; j++)
            {
                var distance = ComputeFingerprintDistance(parsedFingerprints[i], parsedFingerprints[j]);
                if (distance <= threshold)
                    Union(i, j);
            }
        });

        return files
            .Select((f, i) => (f, root: Find(i)))
            .GroupBy(x => x.root)
            .Where(g => g.Count() > 1)
            .Select(g => g.Select(x => x.f).ToList())
            .ToList();
    }

    private static double ComputeFingerprintDistance(ulong[] hashesA, ulong[] hashesB)
    {
        int count = Math.Min(hashesA.Length, hashesB.Length);
        if (count == 0) return 64.0;
        double total = 0;
        for (int i = 0; i < count; i++)
            total += System.Numerics.BitOperations.PopCount(hashesA[i] ^ hashesB[i]);
        return total / count;
    }
}
