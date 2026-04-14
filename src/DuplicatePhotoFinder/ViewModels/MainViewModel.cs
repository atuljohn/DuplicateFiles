using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuplicatePhotoFinder.Models;
using DuplicatePhotoFinder.Services;
using System.Collections.ObjectModel;
using System.Windows;

namespace DuplicatePhotoFinder.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DuplicateDetector _detector;
    private readonly AutoSelectionService _autoSelector;
    private readonly RecycleBinService _recycleBin;
    private readonly ThumbnailService _thumbnailService;
    private readonly PerceptualVerificationService _verifier;
    private readonly DiagnosticsService _diagnostics;

    private CancellationTokenSource? _scanCts;

    [ObservableProperty] private object? _currentView;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isDeletingFiles;

    public ScanProgressViewModel ScanProgress { get; } = new();
    public ScanOptions ScanOptions { get; } = new();
    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = new();
    public SyncViewModel Sync { get; }

    public long TotalWasteBytes => Groups.Sum(g => g.WasteBytes);
    public string TotalWasteDisplay => FormatSize(TotalWasteBytes);
    public int SelectedForDeletionCount => Groups.Sum(g => g.Files.Count(f => f.IsMarkedForDeletion));

    public MainViewModel(
        DuplicateDetector detector,
        AutoSelectionService autoSelector,
        RecycleBinService recycleBin,
        ThumbnailService thumbnailService,
        PerceptualVerificationService verifier,
        SyncViewModel syncViewModel,
        DiagnosticsService diagnostics)
    {
        _detector = detector;
        _autoSelector = autoSelector;
        _recycleBin = recycleBin;
        _thumbnailService = thumbnailService;
        _verifier = verifier;
        _diagnostics = diagnostics;
        Sync = syncViewModel;
        Sync.NavigateTo = view => CurrentView = view;
        CurrentView = "Home";
    }

    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (string.IsNullOrWhiteSpace(ScanOptions.RootFolder) || !Directory.Exists(ScanOptions.RootFolder))
            return;

        Groups.Clear();
        ScanProgress.Reset(); // Reset timer
        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        CurrentView = "Progress";
        _diagnostics.Snapshot("ScanStarted");

        var progress = new Progress<ScanProgressUpdate>(update =>
        {
            ScanProgress.Apply(update);
            _diagnostics.UpdateState(update.Phase, update.FilesProcessed, update.TotalFiles, update.GroupsFound, isBusy: true);
        });

        try
        {
            await foreach (var group in _detector.ScanAsync(ScanOptions, progress, _scanCts.Token))
            {
                _autoSelector.ApplyRecommendation(group, ScanOptions);
                var vm = new DuplicateGroupViewModel(group, _thumbnailService);
                Groups.Add(vm);
                OnPropertyChanged(nameof(TotalWasteDisplay));
                OnPropertyChanged(nameof(SelectedForDeletionCount));
                // Load thumbnails in background
                _ = vm.LoadThumbnailsAsync(_scanCts.Token);
            }
            CurrentView = "Review";
            // Pass 2: verify uncertain groups in background (fire-and-forget, linked to scan cancellation)
            _ = RunPass2Async(_scanCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (Groups.Count > 0)
                CurrentView = "Review";
            else
                CurrentView = "Config";
        }
        finally
        {
            IsScanning = false;
            _diagnostics.MarkIdle("ScanComplete");
        }
    }

    private async Task RunPass2Async(CancellationToken ct)
    {
        // Verify ALL perceptual groups — even H64=0 can be a false positive (empirically confirmed).
        // Exact hash groups are byte-identical so skip those.
        var candidates = Groups
            .Where(g => g.Group.MatchKind == DuplicateMatchKind.Perceptual)
            .ToList();

        if (candidates.Count == 0) return;

        foreach (var vm in candidates)
            vm.IsVerifying = true;

        try
        {
            await foreach (var result in _verifier.VerifyGroupsAsync(
                candidates.Select(v => v.Group), ct))
            {
                var vm = candidates.FirstOrDefault(v => v.Group.Id == result.GroupId);
                if (vm == null) continue;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!result.IsGenuineDuplicate && vm.Files.All(f => !f.IsMarkedForDeletion))
                    {
                        Groups.Remove(vm);
                        OnPropertyChanged(nameof(TotalWasteDisplay));
                        OnPropertyChanged(nameof(SelectedForDeletionCount));
                    }
                    else
                    {
                        vm.PromoteToVerified();
                    }
                });
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Clear any remaining spinners
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                foreach (var vm in candidates.Where(v => Groups.Contains(v)))
                    vm.IsVerifying = false;
            });
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanCts?.Cancel();
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var toDelete = Groups
            .SelectMany(g => g.Files)
            .Where(f => f.IsMarkedForDeletion)
            .Select(f => f.FullPath)
            .ToList();

        if (toDelete.Count == 0) return;

        IsDeletingFiles = true;
        try
        {
            var progress = new Progress<int>(_ =>
            {
                OnPropertyChanged(nameof(TotalWasteDisplay));
                OnPropertyChanged(nameof(SelectedForDeletionCount));
            });

            await _recycleBin.DeleteFilesAsync(toDelete, progress);

            // Remove groups where all files have been deleted
            var groupsToRemove = Groups
                .Where(g => g.Files.All(f => !File.Exists(f.FullPath)))
                .ToList();

            foreach (var g in groupsToRemove)
                Groups.Remove(g);
        }
        finally
        {
            IsDeletingFiles = false;
            OnPropertyChanged(nameof(TotalWasteDisplay));
            OnPropertyChanged(nameof(SelectedForDeletionCount));
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var g in Groups)
            foreach (var f in g.Files.Where(f => !f.IsRecommendedKeep))
                f.IsMarkedForDeletion = true;
        OnPropertyChanged(nameof(TotalWasteDisplay));
        OnPropertyChanged(nameof(SelectedForDeletionCount));
    }

    [RelayCommand]
    private void ClearAll()
    {
        foreach (var g in Groups)
            foreach (var f in g.Files)
                f.IsMarkedForDeletion = false;
        OnPropertyChanged(nameof(TotalWasteDisplay));
        OnPropertyChanged(nameof(SelectedForDeletionCount));
    }

    [RelayCommand]
    private void GoBack()
    {
        CurrentView = "Config";
    }

    [RelayCommand]
    private void OpenDuplicateFinder()
    {
        CurrentView = "Config";
    }

    [RelayCommand]
    private void OpenSync()
    {
        CurrentView = "SyncConfig";
    }

    [RelayCommand]
    private void GoHome()
    {
        CurrentView = "Home";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };
}
