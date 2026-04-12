using DuplicatePhotoFinder.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DuplicatePhotoFinder.Tests.Services;

public class CryptographicHashServiceTests
{
    private readonly CryptographicHashService _service;

    public CryptographicHashServiceTests()
    {
        var logger = new NullLogger<CryptographicHashService>();
        _service = new CryptographicHashService(logger);
    }

    [Fact]
    public async Task ComputeSha256_SameContent_SameHash()
    {
        // Create two temporary files with same content
        var content = "test content for hashing"u8.ToArray();
        var file1 = Path.Combine(Path.GetTempPath(), $"test1_{Guid.NewGuid()}.tmp");
        var file2 = Path.Combine(Path.GetTempPath(), $"test2_{Guid.NewGuid()}.tmp");

        try
        {
            await File.WriteAllBytesAsync(file1, content);
            await File.WriteAllBytesAsync(file2, content);

            var hash1 = await _service.ComputeSha256Async(file1);
            var hash2 = await _service.ComputeSha256Async(file2);

            Assert.NotNull(hash1);
            Assert.NotNull(hash2);
            Assert.Equal(hash1, hash2);
        }
        finally
        {
            File.Delete(file1);
            File.Delete(file2);
        }
    }

    [Fact]
    public async Task ComputeSha256_DifferentContent_DifferentHash()
    {
        var file1 = Path.Combine(Path.GetTempPath(), $"test1_{Guid.NewGuid()}.tmp");
        var file2 = Path.Combine(Path.GetTempPath(), $"test2_{Guid.NewGuid()}.tmp");

        try
        {
            await File.WriteAllTextAsync(file1, "content A");
            await File.WriteAllTextAsync(file2, "content B");

            var hash1 = await _service.ComputeSha256Async(file1);
            var hash2 = await _service.ComputeSha256Async(file2);

            Assert.NotNull(hash1);
            Assert.NotNull(hash2);
            Assert.NotEqual(hash1, hash2);
        }
        finally
        {
            File.Delete(file1);
            File.Delete(file2);
        }
    }

    [Fact]
    public async Task ComputeSha256_NonExistentFile_ReturnsNull()
    {
        var hash = await _service.ComputeSha256Async("/nonexistent/file.tmp");
        Assert.Null(hash);
    }
}
