using DuplicatePhotoFinder.Models;
using DuplicatePhotoFinder.Services;

namespace DuplicatePhotoFinder.Tests.Services;

public class AutoSelectionServiceTests
{
    private readonly AutoSelectionService _service = new();

    [Fact]
    public void ScoreFile_HigherResolution_HigherScore()
    {
        var file1 = new MediaFile { FullPath = "/test/img1.jpg", WidthPixels = 4000, HeightPixels = 3000, FileSizeBytes = 5_000_000 };
        var file2 = new MediaFile { FullPath = "/test/img2.jpg", WidthPixels = 2000, HeightPixels = 1500, FileSizeBytes = 5_000_000 };

        var score1 = _service.ScoreFile(file1, null);
        var score2 = _service.ScoreFile(file2, null);

        Assert.True(score1 > score2, "Higher resolution file should score higher");
    }

    [Fact]
    public void ScoreFile_LargerFile_HigherScore()
    {
        var file1 = new MediaFile { FullPath = "/test/img1.jpg", WidthPixels = 2000, HeightPixels = 1500, FileSizeBytes = 10_000_000 };
        var file2 = new MediaFile { FullPath = "/test/img2.jpg", WidthPixels = 2000, HeightPixels = 1500, FileSizeBytes = 5_000_000 };

        var score1 = _service.ScoreFile(file1, null);
        var score2 = _service.ScoreFile(file2, null);

        Assert.True(score1 > score2, "Larger file should score higher");
    }

    [Fact]
    public void ScoreFile_WithExifDate_HigherScore()
    {
        var now = DateTime.Now;
        var file1 = new MediaFile { FullPath = "/test/img1.jpg", WidthPixels = 2000, HeightPixels = 1500, FileSizeBytes = 5_000_000, DateTaken = now };
        var file2 = new MediaFile { FullPath = "/test/img2.jpg", WidthPixels = 2000, HeightPixels = 1500, FileSizeBytes = 5_000_000, DateTaken = null };

        var score1 = _service.ScoreFile(file1, null);
        var score2 = _service.ScoreFile(file2, null);

        Assert.True(score1 > score2, "File with EXIF date should score higher");
    }

    [Fact]
    public void ScoreFile_BackupPath_LowerScore()
    {
        var file1 = new MediaFile { FullPath = "/Photos/img1.jpg", WidthPixels = 2000, HeightPixels = 1500, FileSizeBytes = 5_000_000 };
        var file2 = new MediaFile { FullPath = "/Backup/img2.jpg", WidthPixels = 2000, HeightPixels = 1500, FileSizeBytes = 5_000_000 };

        var score1 = _service.ScoreFile(file1, null);
        var score2 = _service.ScoreFile(file2, null);

        Assert.True(score1 > score2, "File in backup folder should score lower");
    }

    [Fact]
    public void ScoreFile_PreferredFolder_HigherScore()
    {
        var options = new ScanOptions { PreferredFolderKeywords = new List<string> { "Primary" } };
        var file1 = new MediaFile { FullPath = "/Primary/img1.jpg", WidthPixels = 2000, HeightPixels = 1500, FileSizeBytes = 5_000_000 };
        var file2 = new MediaFile { FullPath = "/Other/img2.jpg", WidthPixels = 2000, HeightPixels = 1500, FileSizeBytes = 5_000_000 };

        var score1 = _service.ScoreFile(file1, options);
        var score2 = _service.ScoreFile(file2, options);

        Assert.True(score1 > score2, "File in preferred folder should score higher");
    }

    [Fact]
    public void ScoreFile_ShallowerPath_HigherScore()
    {
        var file1 = new MediaFile { FullPath = "/Photos/img.jpg", WidthPixels = 2000, HeightPixels = 1500, FileSizeBytes = 5_000_000 };
        var file2 = new MediaFile { FullPath = "/Photos/2023/01/15/img.jpg", WidthPixels = 2000, HeightPixels = 1500, FileSizeBytes = 5_000_000 };

        var score1 = _service.ScoreFile(file1, null);
        var score2 = _service.ScoreFile(file2, null);

        Assert.True(score1 > score2, "Shallower path should score higher");
    }

    [Fact]
    public void ApplyRecommendation_SelectsHighestScorer()
    {
        var group = new DuplicateGroup
        {
            Files = new List<MediaFile>
            {
                new() { FullPath = "/Photos/lowres.jpg", WidthPixels = 1000, HeightPixels = 800, FileSizeBytes = 1_000_000 },
                new() { FullPath = "/Photos/highres.jpg", WidthPixels = 4000, HeightPixels = 3000, FileSizeBytes = 5_000_000 },
                new() { FullPath = "/Backup/copy.jpg", WidthPixels = 4000, HeightPixels = 3000, FileSizeBytes = 5_000_000 }
            }
        };

        _service.ApplyRecommendation(group, null);

        Assert.NotNull(group.RecommendedKeep);
        Assert.Equal("highres.jpg", Path.GetFileName(group.RecommendedKeep.FullPath));
    }
}
