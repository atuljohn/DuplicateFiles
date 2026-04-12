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

    public string MatchKindDisplay => Group.MatchKind switch
    {
        DuplicateMatchKind.Exact => "Exact duplicate",
        DuplicateMatchKind.Perceptual => "Similar image",
        DuplicateMatchKind.Video => "Similar video",
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
        foreach (var file in group.Files)
        {
            var vm = new MediaFileViewModel(file, thumbnailService)
            {
                IsRecommendedKeep = file == group.RecommendedKeep,
                IsMarkedForDeletion = file != group.RecommendedKeep
            };
            Files.Add(vm);
        }
    }

    public async Task LoadThumbnailsAsync(CancellationToken ct = default)
    {
        var tasks = Files.Select(f => f.LoadThumbnailAsync(ct));
        await Task.WhenAll(tasks);
    }

    [RelayCommand]
    private void ToggleKeep(MediaFileViewModel file)
    {
        // Make this file the keep and mark all others for deletion
        foreach (var f in Files)
        {
            f.IsRecommendedKeep = f == file;
            f.IsMarkedForDeletion = f != file;
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
