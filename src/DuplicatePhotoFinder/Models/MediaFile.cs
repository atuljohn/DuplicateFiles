namespace DuplicatePhotoFinder.Models;

public enum MediaKind { Image, Video }

public class MediaFile
{
    public string FullPath { get; init; } = "";
    public string FileName => Path.GetFileName(FullPath);
    public long FileSizeBytes { get; init; }
    public DateTime DateModified { get; init; }
    public DateTime? DateTaken { get; init; }
    public int? WidthPixels { get; init; }
    public int? HeightPixels { get; init; }
    public MediaKind Kind { get; init; }
    public string? ExactHash { get; set; }
    public ulong? PerceptualHash { get; set; }
    public string? VideoFingerprint { get; set; }
}
