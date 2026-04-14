using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuplicatePhotoFinder.Models;
using DuplicatePhotoFinder.Services;
using System.Collections.ObjectModel;

namespace DuplicatePhotoFinder.ViewModels;

public partial class DuplicateGroupViewModel : ObservableObject
{
    public DuplicateGroup Group { get; }
    public ObservableCollection<MediaFileViewModel> Files { get; } = new();

    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private MatchConfidence _confidence;
    [ObservableProperty] private bool _isVerifying;

    public string MatchKindDisplay => (Group.MatchKind, Confidence) switch
    {
        (DuplicateMatchKind.Exact, _) => "Exact duplicate",
        (DuplicateMatchKind.Perceptual, MatchConfidence.High) => "Similar image",
        (DuplicateMatchKind.Perceptual, MatchConfidence.Unconfirmed) => "Possible duplicate",
        (DuplicateMatchKind.Perceptual, MatchConfidence.Verified) => "Verified similar",
        (DuplicateMatchKind.Video, _) => "Similar video",
        _ => "Duplicate"
    };

    public string Summary =>
        $"{Files.Count} files • {FormatSize(Group.TotalWasteBytes)} recoverable";

    public long WasteBytes => Files
        .Where(f => f.IsMarkedForDeletion)
        .Sum(f => f.MediaFile.FileSizeBytes);

    public DuplicateGroupViewModel(DuplicateGroup group, ThumbnailService thumbnailService)
    {
        Group = group;
        _confidence = group.Confidence;

        // Auto-select for deletion only when we're confident (Exact or Verified).
        // Possible/Unconfirmed duplicates start with nothing selected — user decides.
        bool autoSelect = group.MatchKind == DuplicateMatchKind.Exact
                       || group.Confidence == MatchConfidence.Verified;

        foreach (var file in group.Files)
        {
            var vm = new MediaFileViewModel(file, thumbnailService)
            {
                IsRecommendedKeep = autoSelect && file == group.RecommendedKeep,
                IsMarkedForDeletion = autoSelect && file != group.RecommendedKeep
            };
            Files.Add(vm);
        }
    }

    // Called by Pass 2 when a group is promoted from Unconfirmed → Verified
    public void PromoteToVerified()
    {
        // Only update the badge — never auto-select possible duplicates for deletion.
        // User reviews and decides manually.
        Confidence = MatchConfidence.Verified;
        IsVerifying = false;
    }

    public async Task LoadThumbnailsAsync(CancellationToken ct = default)
    {
        var tasks = Files.Select(f => f.LoadThumbnailAsync(ct));
        await Task.WhenAll(tasks);
    }

    [RelayCommand]
    private void KeepFile(MediaFileViewModel file)
    {
        // Toggle keep on this file independently — does not affect other files
        file.IsRecommendedKeep = !file.IsRecommendedKeep;
        // If keeping, unmark for deletion
        if (file.IsRecommendedKeep)
            file.IsMarkedForDeletion = false;
        OnPropertyChanged(nameof(WasteBytes));
    }

    [RelayCommand]
    private void MarkForDeletion(MediaFileViewModel file)
    {
        // Toggle delete on this file independently — does not affect other files
        file.IsMarkedForDeletion = !file.IsMarkedForDeletion;
        // If marking for deletion, unmark keep
        if (file.IsMarkedForDeletion)
            file.IsRecommendedKeep = false;
        OnPropertyChanged(nameof(WasteBytes));
    }

    [RelayCommand]
    private void DeleteAllInGroup()
    {
        foreach (var f in Files)
        {
            f.IsMarkedForDeletion = true;
            f.IsRecommendedKeep = false;
        }
        OnPropertyChanged(nameof(WasteBytes));
    }

    [RelayCommand]
    private void KeepAllInGroup()
    {
        foreach (var f in Files)
        {
            f.IsMarkedForDeletion = false;
        }
        OnPropertyChanged(nameof(WasteBytes));
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };
}
