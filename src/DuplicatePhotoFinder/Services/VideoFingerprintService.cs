using FFMpegCore;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DuplicatePhotoFinder.Services;

public class VideoFingerprintService
{
    private readonly PerceptualHashService _perceptualHash;
    private readonly ILogger<VideoFingerprintService> _logger;

    public VideoFingerprintService(PerceptualHashService perceptualHash, ILogger<VideoFingerprintService> logger)
    {
        _perceptualHash = perceptualHash;
        _logger = logger;
    }

    public async Task<string?> ComputeFingerprintAsync(string path, int frameCount = 8, CancellationToken ct = default)
    {
        try
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
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute video fingerprint: {Path}", path);
            return null;
        }
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
