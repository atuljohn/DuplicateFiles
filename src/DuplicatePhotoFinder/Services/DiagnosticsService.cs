using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace DuplicatePhotoFinder.Services;

/// <summary>
/// Periodic heartbeat + hang watchdog. Writes JSONL to
/// %LOCALAPPDATA%\DuplicatePhotoFinder\diagnostics\heartbeat-{date}.jsonl
/// so post-mortem analysis can correlate the last successful state with a hang.
///
/// Usage: call <see cref="UpdateState"/> from view models / services to publish
/// the current phase + counters. The background loop snapshots that state every
/// HeartbeatIntervalSeconds and appends it to the log along with memory + GC stats.
///
/// The watchdog detects "no progress" — if FilesProcessed and Phase remain
/// unchanged for HangThresholdSeconds while IsBusy is true, it logs a HANG
/// warning containing thread pool stats and the diagnostics directory path
/// (so the user knows where to grab a dump from).
/// </summary>
public class DiagnosticsService : IDisposable
{
    private const int HeartbeatIntervalSeconds = 5;
    private const int HangThresholdSeconds = 60;

    public static string DiagnosticsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuplicatePhotoFinder", "diagnostics");

    private readonly ILogger<DiagnosticsService> _logger;
    private readonly Process _process;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _stateLock = new();
    private readonly string _logPath;

    private string _phase = "Idle";
    private int _filesProcessed;
    private int _totalFiles;
    private int _groupsFound;
    private bool _isBusy;

    private int _lastSeenFilesProcessed = -1;
    private string _lastSeenPhase = "";
    private DateTime _lastProgressUtc = DateTime.UtcNow;
    private bool _hangReported;

    public DiagnosticsService(ILogger<DiagnosticsService> logger)
    {
        _logger = logger;
        _process = Process.GetCurrentProcess();
        Directory.CreateDirectory(DiagnosticsDirectory);
        _logPath = Path.Combine(DiagnosticsDirectory, $"heartbeat-{DateTime.Now:yyyyMMdd}.jsonl");
        WriteEntry(BuildEntry("Startup", isStartup: true));
        _ = Task.Run(LoopAsync);
    }

    /// <summary>
    /// Publish the current scan / sync state. Cheap — call freely from progress callbacks.
    /// </summary>
    public void UpdateState(string phase, int filesProcessed, int totalFiles, int groupsFound, bool isBusy)
    {
        lock (_stateLock)
        {
            _phase = phase;
            _filesProcessed = filesProcessed;
            _totalFiles = totalFiles;
            _groupsFound = groupsFound;
            _isBusy = isBusy;

            if (filesProcessed != _lastSeenFilesProcessed || phase != _lastSeenPhase)
            {
                _lastSeenFilesProcessed = filesProcessed;
                _lastSeenPhase = phase;
                _lastProgressUtc = DateTime.UtcNow;
                _hangReported = false;
            }
        }
    }

    /// <summary>
    /// Mark idle (sync/scan complete or returned to home). Resets watchdog.
    /// </summary>
    public void MarkIdle(string label = "Idle")
    {
        UpdateState(label, 0, 0, 0, isBusy: false);
        WriteEntry(BuildEntry(label));
    }

    /// <summary>
    /// Force an immediate heartbeat write (e.g. just before a long-running step).
    /// </summary>
    public void Snapshot(string note)
    {
        WriteEntry(BuildEntry($"Snapshot:{note}"));
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(HeartbeatIntervalSeconds), _cts.Token);
                WriteEntry(BuildEntry("Heartbeat"));
                CheckForHang();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heartbeat loop error (continuing)");
            }
        }
    }

    private void CheckForHang()
    {
        bool isBusy;
        DateTime lastProgress;
        string phase;
        int processed;
        lock (_stateLock)
        {
            isBusy = _isBusy;
            lastProgress = _lastProgressUtc;
            phase = _phase;
            processed = _filesProcessed;
        }

        if (!isBusy || _hangReported) return;

        var stallSeconds = (DateTime.UtcNow - lastProgress).TotalSeconds;
        if (stallSeconds < HangThresholdSeconds) return;

        _hangReported = true;
        var entry = BuildEntry("HANG_DETECTED");
        entry["stallSeconds"] = (int)stallSeconds;
        entry["lastPhase"] = phase;
        entry["lastFilesProcessed"] = processed;
        entry["dumpHint"] = $"dotnet-dump collect -p {Environment.ProcessId} -o \"{Path.Combine(DiagnosticsDirectory, $"hang-{DateTime.Now:yyyyMMdd-HHmmss}.dmp")}\"";
        WriteEntry(entry);

        _logger.LogError(
            "HANG DETECTED: no progress for {Stall}s in phase '{Phase}' at file {File}. " +
            "Capture a dump with: {Dump}",
            (int)stallSeconds, phase, processed, entry["dumpHint"]);
    }

    private Dictionary<string, object> BuildEntry(string kind, bool isStartup = false)
    {
        _process.Refresh();
        ThreadPool.GetAvailableThreads(out var workerAvail, out var ioAvail);
        ThreadPool.GetMaxThreads(out var workerMax, out var ioMax);

        Dictionary<string, object> entry;
        lock (_stateLock)
        {
            entry = new Dictionary<string, object>
            {
                ["ts"] = DateTime.UtcNow.ToString("O"),
                ["kind"] = kind,
                ["phase"] = _phase,
                ["filesProcessed"] = _filesProcessed,
                ["totalFiles"] = _totalFiles,
                ["groupsFound"] = _groupsFound,
                ["isBusy"] = _isBusy,
            };
        }

        entry["managedMb"] = GC.GetTotalMemory(forceFullCollection: false) / 1_048_576;
        entry["workingSetMb"] = _process.WorkingSet64 / 1_048_576;
        entry["privateMb"] = _process.PrivateMemorySize64 / 1_048_576;
        entry["gen0"] = GC.CollectionCount(0);
        entry["gen1"] = GC.CollectionCount(1);
        entry["gen2"] = GC.CollectionCount(2);
        entry["threadCount"] = _process.Threads.Count;
        entry["workerThreadsBusy"] = workerMax - workerAvail;
        entry["ioThreadsBusy"] = ioMax - ioAvail;
        if (isStartup)
        {
            entry["pid"] = Environment.ProcessId;
            entry["machine"] = Environment.MachineName;
            entry["cpuCount"] = Environment.ProcessorCount;
        }
        return entry;
    }

    private void WriteEntry(Dictionary<string, object> entry)
    {
        try
        {
            var json = JsonSerializer.Serialize(entry);
            File.AppendAllText(_logPath, json + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write diagnostics entry");
        }
    }

    public void Dispose()
    {
        try
        {
            WriteEntry(BuildEntry("Shutdown"));
            _cts.Cancel();
            _cts.Dispose();
            _process.Dispose();
        }
        catch { }
        GC.SuppressFinalize(this);
    }
}
