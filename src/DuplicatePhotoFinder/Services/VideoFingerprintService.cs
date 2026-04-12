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

            for (int i = 0; i < frameCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var offset = i == 0 ? TimeSpan.FromSeconds(1) :
                    TimeSpan.FromSeconds(duration.TotalSeconds * i / (frameCount - 1));
                if (offset >= duration) offset = duration - TimeSpan.FromSeconds(1);
                if (offset < TimeSpan.Zero) offset = TimeSpan.Zero;

                var tempPng = Path.Combine(Path.GetTempPath(), $"dpf_{Guid.NewGuid()}.png");
                try
                {
                    var success = await FFMpegArguments
                        .FromFileInput(path, false, o => o.Seek(offset))
                        .OutputToFile(tempPng, true, o => o.WithFrameOutputCount(1).ForceFormat("image2"))
                        .ProcessAsynchronously();

                    if (success && File.Exists(tempPng))
                    {
                        using var img = await Image.LoadAsync<L8>(tempPng, ct);
                        hashes.Add(_perceptualHash.ComputeDHashFromImage(img));
                    }
                }
                finally
                {
                    if (File.Exists(tempPng)) File.Delete(tempPng);
                }
            }

            return hashes.Count > 0 ? string.Join(",", hashes) : null;
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
