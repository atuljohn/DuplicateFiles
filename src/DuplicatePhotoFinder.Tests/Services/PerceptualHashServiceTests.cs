using DuplicatePhotoFinder.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DuplicatePhotoFinder.Tests.Services;

public class PerceptualHashServiceTests
{
    private readonly PerceptualHashService _service;

    public PerceptualHashServiceTests()
    {
        var logger = new NullLogger<PerceptualHashService>();
        _service = new PerceptualHashService(logger);
    }

    [Fact]
    public async Task ComputeAsync_SameImage_SameHash()
    {
        var file1 = Path.Combine(Path.GetTempPath(), $"img1_{Guid.NewGuid()}.png");
        var file2 = Path.Combine(Path.GetTempPath(), $"img2_{Guid.NewGuid()}.png");

        try
        {
            CreateTestImage(file1);
            CreateTestImage(file2);

            var hash1 = await _service.ComputeAsync(file1);
            var hash2 = await _service.ComputeAsync(file2);

            Assert.NotNull(hash1);
            Assert.NotNull(hash2);
            Assert.Equal(hash1.Value, hash2.Value);
        }
        finally
        {
            File.Delete(file1);
            File.Delete(file2);
        }
    }

    [Fact]
    public async Task ComputeAsync_DifferentImages_DifferentHash()
    {
        var file1 = Path.Combine(Path.GetTempPath(), $"img1_{Guid.NewGuid()}.png");
        var file2 = Path.Combine(Path.GetTempPath(), $"img2_{Guid.NewGuid()}.png");

        try
        {
            CreateTestImageWithPattern(file1, isPattern1: true);
            CreateTestImageWithPattern(file2, isPattern1: false);

            var hash1 = await _service.ComputeAsync(file1);
            var hash2 = await _service.ComputeAsync(file2);

            Assert.NotNull(hash1);
            Assert.NotNull(hash2);
            Assert.NotEqual(hash1.Value, hash2.Value);
        }
        finally
        {
            File.Delete(file1);
            File.Delete(file2);
        }
    }

    [Fact]
    public void HammingDistance_SameHash_ZeroDistance()
    {
        var hash = 0xFFFFFFFFFFFFFFFFul;
        var distance = PerceptualHashService.HammingDistance(hash, hash);
        Assert.Equal(0, distance);
    }

    [Fact]
    public void HammingDistance_InvertedHash_MaxDistance()
    {
        var hash1 = 0x0000000000000000ul;
        var hash2 = 0xFFFFFFFFFFFFFFFFul;
        var distance = PerceptualHashService.HammingDistance(hash1, hash2);
        Assert.Equal(64, distance);
    }

    [Fact]
    public void HammingDistance_OnebitDifference_Distance1()
    {
        var hash1 = 0x0000000000000000ul;
        var hash2 = 0x0000000000000001ul;
        var distance = PerceptualHashService.HammingDistance(hash1, hash2);
        Assert.Equal(1, distance);
    }

    [Fact]
    public async Task ComputeAsync_NonExistentFile_ReturnsNull()
    {
        var hash = await _service.ComputeAsync("/nonexistent/image.png");
        Assert.Null(hash);
    }

    private static void CreateTestImage(string path, byte fillColor = 128)
    {
        using var image = new Image<Rgba32>(100, 100);
        var color = new Rgba32(fillColor, fillColor, fillColor, 255);

        // Fill image with color by setting all pixels
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                image[x, y] = color;
            }
        }

        image.SaveAsPng(path);
    }

    private static void CreateTestImageWithPattern(string path, bool isPattern1)
    {
        using var image = new Image<Rgba32>(100, 100);

        // Create two different patterns (checkerboard vs stripes)
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                byte brightness;
                if (isPattern1)
                {
                    // Checkerboard pattern
                    brightness = ((x / 10 + y / 10) % 2 == 0) ? (byte)50 : (byte)200;
                }
                else
                {
                    // Stripe pattern
                    brightness = (y / 10 % 2 == 0) ? (byte)50 : (byte)200;
                }
                image[x, y] = new Rgba32(brightness, brightness, brightness, 255);
            }
        }

        image.SaveAsPng(path);
    }
}
