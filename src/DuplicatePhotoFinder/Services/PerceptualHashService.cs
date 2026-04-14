using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DuplicatePhotoFinder.Services;

/// <summary>
/// dHash perceptual image comparison. Supports two passes:
/// Pass 1: 32×32 decode → 9×8 dHash (64-bit) + grayscale histogram — fast, one decode per file
/// Pass 2: 128×128 decode → 17×16 dHash (256-bit) — higher precision for uncertain pairs
/// </summary>
public class PerceptualHashService
{
    private readonly ILogger<PerceptualHashService> _logger;

    // Pass 1: JPEG hint stops decode at nearest 1/N DCT scale (~8x speedup on large JPEGs)
    private static readonly DecoderOptions DecoderOpts32 = new()
    {
        TargetSize = new Size(32, 32),
        Configuration = Configuration.Default,
    };

    // Pass 2: higher-res decode for uncertain pairs
    private static readonly DecoderOptions DecoderOpts128 = new()
    {
        TargetSize = new Size(128, 128),
        Configuration = Configuration.Default,
    };

    public PerceptualHashService(ILogger<PerceptualHashService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Pass 1: decode once at 32×32, return both hash and histogram (zero extra I/O).
    /// </summary>
    public Task<(ulong? hash, float[]? histogram)> ComputeWithHistogramAsync(
        string path, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var image = Image.Load<L8>(DecoderOpts32, path);
            var histogram = ComputeHistogram(image);
            image.Mutate(ctx => ctx.Resize(9, 8, KnownResamplers.NearestNeighbor));
            var hash = ComputeDHash(image);
            return Task.FromResult<(ulong?, float[]?)>((hash, histogram));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute perceptual hash: {Path}", path);
            return Task.FromResult<(ulong?, float[]?)>((null, null));
        }
    }

    /// <summary>
    /// Pass 2: decode at 128×128, return 256-bit dHash (4×ulong) for higher discrimination.
    /// </summary>
    public Task<ulong[]?> ComputeHash256Async(string path, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var image = Image.Load<L8>(DecoderOpts128, path);
            image.Mutate(ctx => ctx.Resize(17, 16, KnownResamplers.NearestNeighbor));
            return Task.FromResult<ulong[]?>(ComputeDHash256(image));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute 256-bit perceptual hash: {Path}", path);
            return Task.FromResult<ulong[]?>(null);
        }
    }

    public ulong ComputeDHashFromImage(Image<L8> image)
    {
        if (image.Width != 9 || image.Height != 8)
            image.Mutate(ctx => ctx.Resize(9, 8, KnownResamplers.NearestNeighbor));
        return ComputeDHash(image);
    }

    private static ulong ComputeDHash(Image<L8> image)
    {
        ulong hash = 0;
        ulong bit = 1;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < 8; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < 8; x++)
                {
                    if (row[x].PackedValue > row[x + 1].PackedValue)
                        hash |= bit;
                    bit <<= 1;
                }
            }
        });

        return hash;
    }

    private static ulong[] ComputeDHash256(Image<L8> image)
    {
        // 17×16 grid → 16 rows × 16 comparisons = 256 bits across 4 ulongs
        var hashes = new ulong[4];

        image.ProcessPixelRows(accessor =>
        {
            int bitIndex = 0;
            for (int y = 0; y < 16; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < 16; x++)
                {
                    if (row[x].PackedValue > row[x + 1].PackedValue)
                        hashes[bitIndex / 64] |= 1UL << (bitIndex % 64);
                    bitIndex++;
                }
            }
        });

        return hashes;
    }

    /// <summary>
    /// Compute 256-bucket normalized grayscale histogram from a loaded image.
    /// Fast: single pass over pixels, no I/O.
    /// </summary>
    private static float[] ComputeHistogram(Image<L8> image)
    {
        var hist = new float[256];
        int total = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    hist[row[x].PackedValue]++;
                    total++;
                }
            }
        });

        if (total > 0)
            for (int i = 0; i < 256; i++)
                hist[i] /= total;

        return hist;
    }

    public static int HammingDistance(ulong a, ulong b)
        => System.Numerics.BitOperations.PopCount(a ^ b);

    public static int HammingDistance256(ulong[] a, ulong[] b)
    {
        int dist = 0;
        int count = Math.Min(a.Length, b.Length);
        for (int i = 0; i < count; i++)
            dist += System.Numerics.BitOperations.PopCount(a[i] ^ b[i]);
        return dist;
    }

    /// <summary>
    /// Chi-squared distance between two normalized histograms.
    /// Values > 0.15 indicate different images; < 0.05 very similar.
    /// </summary>
    public static double ChiSquaredDistance(float[] a, float[] b)
    {
        double dist = 0;
        for (int i = 0; i < 256; i++)
        {
            float sum = a[i] + b[i];
            if (sum > 0)
                dist += (a[i] - b[i]) * (a[i] - b[i]) / sum;
        }
        return dist / 2.0;
    }
}
