using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace DuplicatePhotoFinder.Services;

public class CryptographicHashService
{
    private readonly ILogger<CryptographicHashService> _logger;

    public CryptographicHashService(ILogger<CryptographicHashService> logger)
    {
        _logger = logger;
    }

    public async Task<string?> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 131072, useAsync: true);
            using var sha = SHA256.Create();
            var bytes = await sha.ComputeHashAsync(stream, ct);
            return Convert.ToHexString(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to hash file: {Path}", path);
            return null;
        }
    }
}
