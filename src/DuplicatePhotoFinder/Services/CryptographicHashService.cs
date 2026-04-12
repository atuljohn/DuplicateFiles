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
            var fileInfo = new FileInfo(path);

            // For small files (< 50MB), read into memory for faster hashing (fewer kernel round-trips)
            if (fileInfo.Length < 50 * 1024 * 1024)
            {
                var data = await File.ReadAllBytesAsync(path, ct);
                using var sha = SHA256.Create();
                var bytes = sha.ComputeHash(data);
                return Convert.ToHexString(bytes);
            }

            // For large files, use streaming with larger 4MB buffer for NVMe throughput
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 4 * 1024 * 1024, useAsync: true);
            using var sha256 = SHA256.Create();
            var hashBytes = await sha256.ComputeHashAsync(stream, ct);
            return Convert.ToHexString(hashBytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to hash file: {Path}", path);
            return null;
        }
    }
}
