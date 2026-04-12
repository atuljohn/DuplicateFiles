using DuplicatePhotoFinder.Services;
using DuplicatePhotoFinder.ViewModels;
using FFMpegCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System.IO;
using System.Windows;
using WpfApp = System.Windows.Application;

namespace DuplicatePhotoFinder;

public partial class App : WpfApp
{
    public static IServiceProvider Services { get; private set; } = null!;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        // Configure Serilog
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DuplicatePhotoFinder", "deletions.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Month)
            .CreateLogger();

        // Configure FFMpeg binaries
        var toolsDir = Path.Combine(AppContext.BaseDirectory, "tools");
        if (Directory.Exists(toolsDir))
            GlobalFFOptions.Configure(o => o.BinaryFolder = toolsDir);

        // Configure DI
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: true));
        services.AddSingleton<MediaScanner>();
        services.AddSingleton<CryptographicHashService>();
        services.AddSingleton<PerceptualHashService>();
        services.AddSingleton<VideoFingerprintService>();
        services.AddSingleton<ThumbnailService>();
        services.AddSingleton<DuplicateDetector>();
        services.AddSingleton<AutoSelectionService>();
        services.AddSingleton<RecycleBinService>();
        services.AddSingleton<MainViewModel>();

        Services = services.BuildServiceProvider();

        var mainWindow = new MainWindow
        {
            DataContext = Services.GetRequiredService<MainViewModel>()
        };
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
