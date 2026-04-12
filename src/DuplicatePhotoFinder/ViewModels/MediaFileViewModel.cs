using CommunityToolkit.Mvvm.ComponentModel;
using DuplicatePhotoFinder.Models;
using DuplicatePhotoFinder.Services;
using System.Windows.Media.Imaging;

namespace DuplicatePhotoFinder.ViewModels;

public partial class MediaFileViewModel : ObservableObject
{
    private readonly ThumbnailService _thumbnailService;

    public MediaFile MediaFile { get; }

    [ObservableProperty] private bool _isMarkedForDeletion;
    [ObservableProperty] private bool _isRecommendedKeep;
    [ObservableProperty] private BitmapSource? _thumbnail;
    [ObservableProperty] private bool _isLoadingThumbnail = true;

    public string FileName => MediaFile.FileName;
    public string FolderPath => Path.GetDirectoryName(MediaFile.FullPath) ?? "";
    public string FullPath => MediaFile.FullPath;
    public string FileSizeDisplay => FormatSize(MediaFile.FileSizeBytes);
    public string ResolutionDisplay => MediaFile.WidthPixels.HasValue
        ? $"{MediaFile.WidthPixels}×{MediaFile.HeightPixels}"
        : "—";
    public string DateDisplay => (MediaFile.DateTaken ?? MediaFile.DateModified).ToString("yyyy-MM-dd");

    public MediaFileViewModel(MediaFile file, ThumbnailService thumbnailService)
    {
        MediaFile = file;
        _thumbnailService = thumbnailService;
    }

    public async Task LoadThumbnailAsync(CancellationToken ct = default)
    {
        IsLoadingThumbnail = true;
        try
        {
            Thumbnail = await _thumbnailService.GetThumbnailAsync(MediaFile.FullPath, ct);
        }
        finally
        {
            IsLoadingThumbnail = false;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };
}
