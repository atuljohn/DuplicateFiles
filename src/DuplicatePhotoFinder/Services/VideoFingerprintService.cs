using FFMpegCore;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Diagnostics;

namespace DuplicatePhotoFinder.Services;

public class VideoFingerprintService
{
    private readonly PerceptualHashService _perceptualHash;
    private readonly ILogger<VideoFingerprintService> _logger;

    // Timeout for individual video decode operations. Videos that take longer
    // than this are likely corrupted or have unsupported codecs — skip them.
    private const int VideoFingerprintTimeoutSeconds = 30;

    public VideoFingerprintService(PerceptualHashService perceptualHash, ILogger<VideoFingerprintService> logger)
    {
        _perceptualHash = perceptualHash;
        _logger = logger;
    }

    public async Task<string?> ComputeFingerprintAsync(string path, int frameCount = 8, CancellationToken ct = default)
    {
        try
        {
            // Wrap the entire operation with a timeout. If FFmpeg hangs on a corrupted
            // or unsupported file, we abort after 30 seconds and skip the video.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(VideoFingerprintTimeoutSeconds));

            var task = ComputeFingerprintInternalAsync(path, frameCount, timeoutCts.Token);
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Video fingerprint timed out ({TimeoutSec}s): {Path}",
                VideoFingerprintTimeoutSeconds, path);
            KillStrayFFmpegProcesses();
            return null; // Skip this video
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute video fingerprint: {Path}", path);
            return null;
        }
    }

    private async Task<string?> ComputeFingerprintInternalAsync(string path, int frameCount, CancellationToken ct)
    {
        var mediaInfo = await FFProbe.AnalyseAsync(path, null, ct);
        var duration = mediaInfo.Duration;
        if (duration.TotalSeconds < 1) return null;

        var hashes = new List<ulong>();
        var tempDir = Path.Combine(Path.GetTempPath(), $"dpf_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Single FFmpeg pass: extract frameCount evenly distributed frames
            // This replaces 8 separate ffmpeg processes with 1 → massive speedup
            var framePattern = Path.Combine(tempDir, "frame_%05d.png");

            var success = await FFMpegArguments
                .FromFileInput(path)
                .OutputToFile(framePattern, true, o => o
                    .ForceFormat("image2")
                    .WithFrameOutputCount(frameCount))
                .ProcessAsynchronously();

            if (success)
            {
                // Load all extracted frames and compute hashes
                var frameFiles = Directory.GetFiles(tempDir, "frame_*.png")
                    .OrderBy(f => f)
                    .ToList();

                foreach (var frameFile in frameFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var img = await Image.LoadAsync<L8>(frameFile, ct);
                        hashes.Add(_perceptualHash.ComputeDHashFromImage(img));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to process frame: {Frame}", frameFile);
                    }
                }
            }

            return hashes.Count > 0 ? string.Join(",", hashes) : null;
        }
        finally
        {
            // Cleanup temp directory
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch { /* ignore cleanup errors */ }
            }
            // Kill any lingering FFmpeg processes that didn't exit cleanly.
            // This is crucial if the timeout fired or the file was corrupted.
            KillStrayFFmpegProcesses();
        }
    }

    /// <summary>
    /// Kill any ffmpeg.exe processes started within the last 35 seconds that didn't
    /// exit cleanly. This catches hangs from corrupted files or unsupported codecs.
    /// </summary>
    private static void KillStrayFFmpegProcesses()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-35);
            foreach (var proc in Process.GetProcessesByName("ffmpeg"))
            {
                try
                {
                    // Only kill recent ffmpeg instances (started within our timeout window).
                    // Avoids killing unrelated ffmpeg processes the user might be running.
                    if (proc.StartTime.ToUniversalTime() > cutoff && !proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch { /* ignore per-process kill errors */ }
            }
        }
        catch { /* ignore process enumeration errors */ }
    }

    public static double FingerprintDistance(string a, string b)
    {
        var hashesA = a.Split(',').Select(ulong.Parse).ToArray();
        var hashesB = b.Split(',').Select(ulong.Parse).ToArray();
        int count = Math.Min(hashesA.Length, hashesB.Length);
        if (count == 0) return 64.0;
        double total = 0;
        for (int i = 0; i < count; i++)
            total += System.Numerics.BitOperations.PopCount(hashesA[i] ^ hashesB[i]);
        return total / count;
    }
}
