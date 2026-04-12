namespace DuplicatePhotoFinder.Models;

public class ScanOptions
{
    public string RootFolder { get; set; } = "";
    public bool DetectExact { get; set; } = true;
    public bool DetectPerceptual { get; set; } = true;
    public bool DetectVideo { get; set; } = true;
    public int PerceptualThreshold { get; set; } = 10; // Hamming distance 0-64
    public int VideoFrameCount { get; set; } = 8;
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
