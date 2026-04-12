using DuplicatePhotoFinder.Models;

namespace DuplicatePhotoFinder.Tests.Models;

public class ScanOptionsTests
{
    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".png")]
    [InlineData(".gif")]
    [InlineData(".bmp")]
    [InlineData(".heic")]
    public void IsMediaFile_ImageExtensions_ReturnsTrue(string ext)
    {
        var options = new ScanOptions();
        Assert.True(options.IsMediaFile(ext));
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mov")]
    [InlineData(".avi")]
    [InlineData(".mkv")]
    [InlineData(".wmv")]
    public void IsMediaFile_VideoExtensions_ReturnsTrue(string ext)
    {
        var options = new ScanOptions();
        Assert.True(options.IsMediaFile(ext));
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".doc")]
    [InlineData(".zip")]
    [InlineData(".exe")]
    public void IsMediaFile_NonMediaExtensions_ReturnsFalse(string ext)
    {
        var options = new ScanOptions();
        Assert.False(options.IsMediaFile(ext));
    }

    [Fact]
    public void IsMediaFile_CaseInsensitive()
    {
        var options = new ScanOptions();
        Assert.True(options.IsMediaFile(".JPG"));
        Assert.True(options.IsMediaFile(".JpG"));
        Assert.True(options.IsMediaFile(".MP4"));
    }

    [Fact]
    public void ScanOptions_DefaultValues()
    {
        var options = new ScanOptions();
        Assert.True(options.DetectExact);
        Assert.True(options.DetectPerceptual);
        Assert.True(options.DetectVideo);
        Assert.Equal(10, options.PerceptualThreshold);
        Assert.Equal(8, options.VideoFrameCount);
    }
}
