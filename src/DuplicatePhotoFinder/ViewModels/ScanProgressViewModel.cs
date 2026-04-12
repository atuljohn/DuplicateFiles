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

    public void Apply(ScanProgressUpdate update)
    {
        Phase = update.Phase;
        FilesProcessed = update.FilesProcessed;
        TotalFiles = update.TotalFiles;
        GroupsFound = update.GroupsFound;
        CurrentFile = update.CurrentFile;
        ProgressPercent = TotalFiles > 0 ? (double)FilesProcessed / TotalFiles * 100 : 0;
    }
}
