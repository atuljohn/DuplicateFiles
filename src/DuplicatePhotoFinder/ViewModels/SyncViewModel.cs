using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuplicatePhotoFinder.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows;

namespace DuplicatePhotoFinder.ViewModels;

public partial class SyncViewModel : ObservableObject
{
    private readonly SyncService _syncService;
    private CancellationTokenSource? _cts;
    private List<SyncAction>? _currentPlan;

    // ── Bindable options ─────────────────────────────────────────────────────

    [ObservableProperty] private string _sourceFolder = string.Empty;
    [ObservableProperty] private string _destFolder = string.Empty;

    [ObservableProperty] private bool _modeOneWay = true;
    [ObservableProperty] private bool _modeMirror;
    [ObservableProperty] private bool _modeTwoWay;

    [ObservableProperty] private bool _compareByDate = true;
    [ObservableProperty] private bool _compareBySizeAndDate;

    [ObservableProperty] private bool _previewBeforeSync = true;

    // ── Progress / result state ───────────────────────────────────────────────

    [ObservableProperty] private string _phase = string.Empty;
    [ObservableProperty] private int _totalFiles;
    [ObservableProperty] private int _processedFiles;
    [ObservableProperty] private long _bytesTransferred;
    [ObservableProperty] private string _currentFile = string.Empty;
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private bool _isPreviewReady;

    // ── Result log ───────────────────────────────────────────────────────────

    public ObservableCollection<SyncAction> Actions { get; } = new();

    // ── Computed summary ─────────────────────────────────────────────────────

    public int PlanCopyCount => _currentPlan?.Count(a => a.ActionType == SyncActionType.CopyToDestination) ?? 0;
    public int PlanCopyBackCount => _currentPlan?.Count(a => a.ActionType == SyncActionType.CopyToSource) ?? 0;
    public int PlanDeleteCount => _currentPlan?.Count(a => a.ActionType == SyncActionType.DeleteFromDestination) ?? 0;

    public int CopiedCount => Actions.Count(a => a.ActionType == SyncActionType.CopyToDestination && a.Status == SyncActionStatus.Completed);
    public int CopiedBackCount => Actions.Count(a => a.ActionType == SyncActionType.CopyToSource && a.Status == SyncActionStatus.Completed);
    public int DeletedCount => Actions.Count(a => a.ActionType == SyncActionType.DeleteFromDestination && a.Status == SyncActionStatus.Completed);
    public int ErrorCount => Actions.Count(a => a.Status == SyncActionStatus.Error);
    public int SkippedCount => Actions.Count(a => a.Status == SyncActionStatus.Skipped);

    public string BytesTransferredDisplay => FormatSize(BytesTransferred);

    public bool HasPendingPlan => IsPreviewReady && _currentPlan?.Count > 0;
    public bool PlanIsEmpty => IsPreviewReady && (_currentPlan == null || _currentPlan.Count == 0);

    // Navigation callback — set by MainViewModel
    public Action<string>? NavigateTo { get; set; }

    public SyncViewModel(SyncService syncService)
    {
        _syncService = syncService;
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void BrowseSource()
    {
        var dlg = new OpenFolderDialog { Title = "Select Source Folder" };
        if (dlg.ShowDialog() == true)
            SourceFolder = dlg.FolderName;
    }

    [RelayCommand]
    private void BrowseDest()
    {
        var dlg = new OpenFolderDialog { Title = "Select Destination Folder" };
        if (dlg.ShowDialog() == true)
            DestFolder = dlg.FolderName;
    }

    [RelayCommand]
    private async Task StartSyncAsync()
    {
        if (!ValidateFolders()) return;

        Actions.Clear();
        _currentPlan = null;
        IsPreviewReady = false;
        _cts = new CancellationTokenSource();
        IsSyncing = true;
        NavigateTo?.Invoke("SyncProgress");

        var options = BuildOptions();
        var progress = new Progress<SyncProgressUpdate>(Apply);

        try
        {
            _currentPlan = await _syncService.BuildPlanAsync(options, progress, _cts.Token);

            if (PreviewBeforeSync)
            {
                // Show preview result — user can review and then Execute
                Phase = "Preview ready";
                IsPreviewReady = true;
                IsSyncing = false;
                NotifySummary();
                NavigateTo?.Invoke("SyncResult");
            }
            else
            {
                await RunExecutionAsync(options, _currentPlan, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Phase = "Cancelled";
            IsSyncing = false;
            NavigateTo?.Invoke("SyncConfig");
        }
        finally
        {
            IsSyncing = false;
        }
    }

    [RelayCommand]
    private async Task ExecuteSyncAsync()
    {
        if (_currentPlan == null || !IsPreviewReady) return;

        Actions.Clear();
        IsPreviewReady = false;
        _cts = new CancellationTokenSource();
        IsSyncing = true;
        NavigateTo?.Invoke("SyncProgress");

        var options = BuildOptions();
        try
        {
            await RunExecutionAsync(options, _currentPlan, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Phase = "Cancelled";
            IsSyncing = false;
            NavigateTo?.Invoke("SyncResult");
        }
        finally
        {
            IsSyncing = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private void GoBackToConfig()
    {
        _cts?.Cancel();
        IsPreviewReady = false;
        _currentPlan = null;
        Actions.Clear();
        NavigateTo?.Invoke("SyncConfig");
    }

    [RelayCommand]
    private void GoHome()
    {
        _cts?.Cancel();
        IsPreviewReady = false;
        _currentPlan = null;
        Actions.Clear();
        NavigateTo?.Invoke("Home");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task RunExecutionAsync(SyncOptions options, List<SyncAction> plan, CancellationToken ct)
    {
        Phase = "Executing sync...";
        TotalFiles = plan.Count;
        ProcessedFiles = 0;

        var progress = new Progress<SyncProgressUpdate>(Apply);

        await foreach (var action in _syncService.ExecuteAsync(options, plan, progress, ct))
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Actions.Add(action);
                NotifySummary();
            });
        }

        Phase = "Sync complete";
        IsSyncing = false;
        NotifySummary();
        NavigateTo?.Invoke("SyncResult");
    }

    private void Apply(SyncProgressUpdate update)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Phase = update.Phase;
            TotalFiles = update.TotalFiles;
            ProcessedFiles = update.ProcessedFiles;
            BytesTransferred = update.BytesTransferred;
            CurrentFile = update.CurrentFile;
            OnPropertyChanged(nameof(BytesTransferredDisplay));
        });
    }

    private SyncOptions BuildOptions() => new(
        SourceFolder: SourceFolder,
        DestFolder: DestFolder,
        Mode: ModeMirror ? SyncMode.Mirror : ModeTwoWay ? SyncMode.TwoWay : SyncMode.OneWayCopy,
        CompareMethod: CompareBySizeAndDate ? SyncCompareMethod.SizeAndDate : SyncCompareMethod.ModifiedDate,
        PreviewOnly: PreviewBeforeSync);

    private bool ValidateFolders()
    {
        if (string.IsNullOrWhiteSpace(SourceFolder) || !Directory.Exists(SourceFolder))
        {
            MessageBox.Show("Please select a valid source folder.", "Invalid Source", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (string.IsNullOrWhiteSpace(DestFolder) || !Directory.Exists(DestFolder))
        {
            MessageBox.Show("Please select a valid destination folder.", "Invalid Destination", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (string.Equals(SourceFolder.TrimEnd('\\', '/'),
                          DestFolder.TrimEnd('\\', '/'),
                          StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Source and destination folders must be different.", "Same Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private void NotifySummary()
    {
        OnPropertyChanged(nameof(PlanCopyCount));
        OnPropertyChanged(nameof(PlanCopyBackCount));
        OnPropertyChanged(nameof(PlanDeleteCount));
        OnPropertyChanged(nameof(CopiedCount));
        OnPropertyChanged(nameof(CopiedBackCount));
        OnPropertyChanged(nameof(DeletedCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(SkippedCount));
        OnPropertyChanged(nameof(HasPendingPlan));
        OnPropertyChanged(nameof(PlanIsEmpty));
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };
}
