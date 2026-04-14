namespace DuplicatePhotoFinder.Models;

public enum DuplicateMatchKind { Exact, Perceptual, Video }
public enum MatchConfidence { High, Unconfirmed, Verified }

public class DuplicateGroup
{
    public Guid Id { get; } = Guid.NewGuid();
    public DuplicateMatchKind MatchKind { get; init; }
    public List<MediaFile> Files { get; init; } = new();
    public MediaFile? RecommendedKeep { get; set; }
    public MatchConfidence Confidence { get; set; } = MatchConfidence.High;
    public int MinHammingDistance { get; set; }

    public long TotalWasteBytes =>
        Files.Sum(f => f.FileSizeBytes) - (RecommendedKeep?.FileSizeBytes ?? Files.Max(f => f.FileSizeBytes));
}
