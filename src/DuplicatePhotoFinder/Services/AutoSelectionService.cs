using DuplicatePhotoFinder.Models;

namespace DuplicatePhotoFinder.Services;

public class AutoSelectionService
{
    public void ApplyRecommendation(DuplicateGroup group, ScanOptions? options = null)
    {
        if (group.Files.Count == 0) return;

        var scored = group.Files
            .Select(f => (file: f, score: ScoreFile(f, options)))
            .OrderByDescending(x => x.score)
            .ToList();

        group.RecommendedKeep = scored[0].file;
    }

    public int ScoreFile(MediaFile file, ScanOptions? options)
    {
        int score = 0;

        // Resolution bonus
        if (file.WidthPixels.HasValue && file.HeightPixels.HasValue)
            score += (file.WidthPixels.Value * file.HeightPixels.Value) / 100_000;

        // File size bonus (larger = less compressed)
        score += (int)(file.FileSizeBytes / 1_048_576);

        // Preferred folder keywords
        if (options != null)
            foreach (var keyword in options.PreferredFolderKeywords)
                if (file.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    score += 1000;

        // Penalize backup/temp/copy paths
        var lower = file.FullPath.ToLowerInvariant();
        if (lower.Contains("backup") || lower.Contains("\\temp\\") || lower.Contains("/temp/") ||
            lower.Contains("copy") || lower.Contains("duplicate") || lower.Contains("_copy"))
            score -= 500;

        // EXIF date bonus
        if (file.DateTaken.HasValue) score += 200;

        // Shallower path = more intentional organization
        score -= file.FullPath.Count(c => c == '\\' || c == '/') * 10;

        return score;
    }
}
