using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DuplicatePhotoFinder.Services;

/// <summary>
/// Implements the Difference Hash (dHash) algorithm for perceptual image comparison.
/// Resizes image to 9x8, converts to grayscale, then computes 64-bit hash from
/// adjacent pixel brightness differences.
/// </summary>
public class PerceptualHashService
{
    private readonly ILogger<PerceptualHashService> _logger;

    public PerceptualHashService(ILogger<PerceptualHashService> logger)
    {
        _logger = logger;
    }

    public async Task<ulong?> ComputeAsync(string path, CancellationToken ct = default)
    {
        try
        {
            using var image = await Image.LoadAsync<L8>(path, ct);
            return ComputeDHash(image);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute perceptual hash: {Path}", path);
            return null;
        }
    }

    public ulong ComputeDHashFromImage(Image<L8> image)
        => ComputeDHash(image);

    private static ulong ComputeDHash(Image<L8> image)
    {
        // Resize to 9x8
        var copy = image.Clone(ctx => ctx.Resize(9, 8, KnownResamplers.Bicubic));

        ulong hash = 0;
        ulong bit = 1;

        copy.ProcessPixelRows(accessor =>
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

    public static int HammingDistance(ulong a, ulong b)
    {
        ulong xor = a ^ b;
        return System.Numerics.BitOperations.PopCount(xor);
    }
}
