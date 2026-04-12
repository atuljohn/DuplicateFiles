using CommunityToolkit.Mvvm.ComponentModel;

namespace DuplicatePhotoFinder.Models;

public partial class ScanOptions : ObservableObject
{
    [ObservableProperty]
    private string rootFolder = "";

    [ObservableProperty]
    private bool detectExact = true;

    [ObservableProperty]
    private bool detectPerceptual = true;

    [ObservableProperty]
    private bool detectVideo = true;

    [ObservableProperty]
    private int perceptualThreshold = 10; // Hamming distance 0-64

    [ObservableProperty]
    private int videoFrameCount = 8;

    public List<string> PreferredFolderKeywords { get; set; } = new();

    public HashSet<string> ImageExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif", ".heic", ".heif", ".webp"
    };

    public HashSet<string> VideoExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".flv", ".m4v", ".3gp"
    };

    public bool IsMediaFile(string extension) =>
        ImageExtensions.Contains(extension) || VideoExtensions.Contains(extension);
}
