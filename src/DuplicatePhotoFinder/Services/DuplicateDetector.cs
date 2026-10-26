using DuplicatePhotoFinder.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

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
        PauseGate? pauseGate = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        pauseGate ??= new PauseGate();

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

        var sizeGroups = allFiles.GroupBy(f => f.FileSizeBytes)
            .Where(g => g.Count() > 1)
            .ToList();

        var sizeGroupFiles = sizeGroups.SelectMany(g => g).ToList();

        var processed = 0;
        var groupsFound = 0;
        long lastProgressTick = 0;

        // Phase 2: Exact hash matching — two-pass prefix→full to cut I/O
        if (options.DetectExact)
        {
            // 2a: Read first 64 KB of each same-size file
            progress?.Report(new ScanProgressUpdate("Prefix-scanning files...", 0, total, groupsFound, ""));

            var prefixData = new ConcurrentDictionary<string, (string? hash, bool isComplete)>();
            var sem = new SemaphoreSlim(Environment.ProcessorCount * 2);
            var tasks = new List<Task>();

            foreach (var file in sizeGroupFiles)
            {
                ct.ThrowIfCancellationRequested();
                await sem.WaitAsync(ct);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await pauseGate.WaitAsync(ct);
                        var result = await _cryptoHash.ComputePrefixHashAsync(file.FullPath, ct);
                        prefixData[file.FullPath] = result;

                        // For files ≤ 64 KB the prefix IS the full SHA-256
                        if (result.hash != null && result.isComplete)
                            file.ExactHash = result.hash;
                    }
                    finally
                    {
                        sem.Release();
                        var p = Interlocked.Increment(ref processed);
                        ThrottledProgress(ref lastProgressTick, progress,
                            "Prefix-scanning files...", p, total, groupsFound, file.FullPath);
                    }
                }, ct));
            }
            await Task.WhenAll(tasks);

            // 2b: Full-hash only files whose (size, prefix) matches another file
            var needFullHash = sizeGroupFiles
                .Where(f => f.ExactHash == null)
                .Where(f => prefixData.TryGetValue(f.FullPath, out var pd) && pd.hash != null)
                .GroupBy(f => (f.FileSizeBytes, prefixData[f.FullPath].hash!))
                .Where(g => g.Count() > 1)
                .SelectMany(g => g)
                .ToList();

            _logger.LogInformation(
                "Prefix scan: {SizeGroupCount} same-size files, {NeedFull} need full hash",
                sizeGroupFiles.Count, needFullHash.Count);

            if (needFullHash.Count > 0)
            {
                progress?.Report(new ScanProgressUpdate(
                    $"Full-hashing {needFullHash.Count} candidates...",
                    processed, total, groupsFound, ""));

                tasks.Clear();
                foreach (var file in needFullHash)
                {
                    ct.ThrowIfCancellationRequested();
                    await sem.WaitAsync(ct);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await pauseGate.WaitAsync(ct);
                            file.ExactHash = await _cryptoHash.ComputeSha256Async(file.FullPath, ct);
                        }
                        finally
                        {
                            sem.Release();
                            var p = Interlocked.Increment(ref processed);
                            ThrottledProgress(ref lastProgressTick, progress,
                                "Full-hashing candidates...", p, total, groupsFound, file.FullPath);
                        }
                    }, ct));
                }
                await Task.WhenAll(tasks);
            }

            var exactGroups = sizeGroupFiles
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

            var sem = new SemaphoreSlim(Environment.ProcessorCount * 2);
            var tasks = new List<Task>();

            foreach (var file in imageFiles)
            {
                ct.ThrowIfCancellationRequested();
                await sem.WaitAsync(ct);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await pauseGate.WaitAsync(ct);
                        var (hash, _) = await _perceptualHash.ComputeWithHistogramAsync(file.FullPath, ct);
                        file.PerceptualHash = hash;
                    }
                    finally
                    {
                        sem.Release();
                        var p = Interlocked.Increment(ref processed);
                        ThrottledProgress(ref lastProgressTick, progress,
                            "Computing perceptual hashes...", p, total, groupsFound, file.FullPath);
                    }
                }, ct));
            }

            await Task.WhenAll(tasks);

            SixLabors.ImageSharp.Configuration.Default.MemoryAllocator.ReleaseRetainedResources();

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

        // Phase 4: Video fingerprinting — allow 2 concurrent FFmpeg processes
        if (options.DetectVideo)
        {
            progress?.Report(new ScanProgressUpdate("Computing video fingerprints...", processed, total, groupsFound, ""));

            var videoFiles = allFiles
                .Where(f => f.Kind == MediaKind.Video && f.ExactHash == null)
                .ToList();

            var sem = new SemaphoreSlim(2);
            var tasks = new List<Task>();

            foreach (var file in videoFiles)
            {
                ct.ThrowIfCancellationRequested();
                await sem.WaitAsync(ct);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await pauseGate.WaitAsync(ct);
                        file.VideoFingerprint = await _videoFingerprint.ComputeFingerprintAsync(
                            file.FullPath, options.VideoFrameCount, ct);
                    }
                    finally
                    {
                        sem.Release();
                        var p = Interlocked.Increment(ref processed);
                        ThrottledProgress(ref lastProgressTick, progress,
                            "Computing video fingerprints...", p, total, groupsFound, file.FullPath);
                    }
                }, ct));
            }

            await Task.WhenAll(tasks);

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

    private static void ThrottledProgress(ref long lastTick, IProgress<ScanProgressUpdate>? progress,
        string phase, int processed, int total, int groupsFound, string currentFile)
    {
        if (progress == null) return;
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref lastTick);
        if (now - last >= 100 || processed == total)
        {
            if (Interlocked.CompareExchange(ref lastTick, now, last) == last)
                progress.Report(new ScanProgressUpdate(phase, processed, total, groupsFound, currentFile));
        }
    }

    private (List<(List<MediaFile> files, int minDist)> highConf, List<(List<MediaFile> files, int minDist)> uncertain)
        FindPerceptualGroupsParallel(List<MediaFile> files, int threshold, int highConfThreshold)
    {
        if (files.Count == 0) return (new(), new());

        var parent = Enumerable.Range(0, files.Count).ToArray();
        var parentLock = new object();
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

        var parsedFingerprints = files.Select(f =>
            f.VideoFingerprint!.Split(',').Select(ulong.Parse).ToArray()).ToList();

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
