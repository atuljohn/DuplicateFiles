using Microsoft.Win32;

namespace DuplicatePhotoFinder.Views;

public partial class ScanConfigView
{
    public ScanConfigView()
    {
        InitializeComponent();
    }

    private void BrowseButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select root folder to scan",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FolderName))
        {
            if (DataContext is ViewModels.MainViewModel vm)
                vm.ScanOptions.RootFolder = dialog.FolderName;
        }
    }
}
