using CommunityToolkit.Mvvm.ComponentModel;
using DuplicatePhotoFinder.Services;

namespace DuplicatePhotoFinder.ViewModels;

public partial class ScanProgressViewModel : ObservableObject
{
    [ObservableProperty] private string _phase = "Initializing...";
    [ObservableProperty] private int _filesProcessed;
    [ObservableProperty] private int _totalFiles;
    [ObservableProperty] private int _groupsFound;
    [ObservableProperty] private string _currentFile = "";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _elapsedTime = "00:00";
    [ObservableProperty] private string _estimatedRemainingTime = "--:--";

    private DateTime _startTime;

    public ScanProgressViewModel()
    {
        Reset();
    }

    public void Reset()
    {
        _startTime = DateTime.Now;
        Phase = "Initializing...";
        FilesProcessed = 0;
        TotalFiles = 0;
        GroupsFound = 0;
        CurrentFile = "";
        ProgressPercent = 0;
        ElapsedTime = "00:00";
        EstimatedRemainingTime = "--:--";
    }

    public void Apply(ScanProgressUpdate update)
    {
        Phase = update.Phase;
        FilesProcessed = update.FilesProcessed;
        TotalFiles = update.TotalFiles;
        GroupsFound = update.GroupsFound;
        CurrentFile = update.CurrentFile;
        ProgressPercent = TotalFiles > 0 ? (double)FilesProcessed / TotalFiles * 100 : 0;

        // Update timers
        var elapsed = DateTime.Now - _startTime;
        ElapsedTime = FormatTime(elapsed);

        if (ProgressPercent > 0 && ProgressPercent < 100)
        {
            var estimatedTotal = elapsed.TotalSeconds / (ProgressPercent / 100.0);
            var remaining = TimeSpan.FromSeconds(estimatedTotal - elapsed.TotalSeconds);
            EstimatedRemainingTime = FormatTime(remaining);
        }
        else if (ProgressPercent >= 100)
        {
            EstimatedRemainingTime = "00:00";
        }
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}";
        return $"{time.Minutes}:{time.Seconds:D2}";
    }
}
