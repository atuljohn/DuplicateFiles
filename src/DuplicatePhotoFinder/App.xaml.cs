using DuplicatePhotoFinder.Services;
using DuplicatePhotoFinder.ViewModels;
using FFMpegCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System.IO;
using System.Windows;
using WpfApp = System.Windows.Application;

namespace DuplicatePhotoFinder;

public partial class App : WpfApp
{
    public static IServiceProvider Services { get; private set; } = null!;
    private static Serilog.ILogger? _logger;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        try
        {
            // Configure Serilog
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DuplicatePhotoFinder", "deletions.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

            var debugLogPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DuplicatePhotoFinder", "debug.log");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Month)
                .WriteTo.File(debugLogPath, rollingInterval: RollingInterval.Day)
                .CreateLogger();

            _logger = Log.ForContext<App>();
            _logger.Information("Application starting");

            // Configure FFMpeg binaries
            var toolsDir = Path.Combine(AppContext.BaseDirectory, "tools");
            if (Directory.Exists(toolsDir))
                GlobalFFOptions.Configure(o => o.BinaryFolder = toolsDir);
            else
                _logger.Warning("FFmpeg tools directory not found at: {ToolsDir}", toolsDir);

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
            services.AddSingleton<PerceptualVerificationService>();
            services.AddSingleton<SyncService>();
            services.AddSingleton<SyncViewModel>();
            services.AddSingleton<MainViewModel>();

            Services = services.BuildServiceProvider();
            _logger.Information("Dependency injection configured successfully");

            var mainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>()
            };
            mainWindow.Show();
            _logger.Information("Main window displayed");
        }
        catch (Exception ex)
        {
            _logger?.Fatal(ex, "Fatal error during application startup");
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DuplicatePhotoFinder", "crash.log");
            File.WriteAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}");
            MessageBox.Show($"Application startup failed:\n\n{ex.Message}\n\nCheck {logPath} for details.",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Information("Application exiting with code {ExitCode}", e.ApplicationExitCode);
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private void Application_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Fatal(e.Exception, "Unhandled dispatcher exception");
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DuplicatePhotoFinder", "crash.log");
        File.AppendAllText(logPath, $"\n{DateTime.Now:yyyy-MM-dd HH:mm:ss}\nUnhandled Exception:\n{e.Exception}\n");
        MessageBox.Show($"An unexpected error occurred:\n\n{e.Exception.Message}\n\nCheck {logPath} for details.",
            "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = false; // Let the app crash so we see it
    }
}
