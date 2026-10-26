using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace DuplicatePhotoFinder.Services;

public class CryptographicHashService
{
    private readonly ILogger<CryptographicHashService> _logger;
    private const int PrefixSize = 64 * 1024;
    private const int LargeBuffer = 4 * 1024 * 1024;
    private const long SmallFileThreshold = 50 * 1024 * 1024;

    public CryptographicHashService(ILogger<CryptographicHashService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Hash only the first 64 KB of a file. For files ≤ 64 KB the prefix
    /// covers the entire file so isComplete=true and the hash equals the full SHA-256.
    /// </summary>
    public async Task<(string? hash, bool isComplete)> ComputePrefixHashAsync(
        string path, CancellationToken ct = default)
    {
        try
        {
            var buffer = ArrayPool<byte>.Shared.Rent(PrefixSize);
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, bufferSize: PrefixSize, useAsync: true);

                int filled = 0;
                int read;
                while (filled < PrefixSize &&
                       (read = await stream.ReadAsync(buffer.AsMemory(filled, PrefixSize - filled), ct)) > 0)
                {
                    filled += read;
                }

                var hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, filled)));
                bool isComplete = stream.Position >= stream.Length;
                return (hash, isComplete);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prefix-hash file: {Path}", path);
            return (null, false);
        }
    }

    public async Task<string?> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        try
        {
            var fileInfo = new FileInfo(path);

            if (fileInfo.Length < SmallFileThreshold)
            {
                var data = await File.ReadAllBytesAsync(path, ct);
                return Convert.ToHexString(SHA256.HashData(data));
            }

            // Large files: incremental hash with pooled 4 MB buffer
            // (IncrementalHash + explicit ReadAsync beats ComputeHashAsync's internal 4 KB reads)
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: LargeBuffer, useAsync: true);

            var buffer = ArrayPool<byte>.Shared.Rent(LargeBuffer);
            try
            {
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, LargeBuffer), ct)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                }
                return Convert.ToHexString(hasher.GetHashAndReset());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to hash file: {Path}", path);
            return null;
        }
    }
}
