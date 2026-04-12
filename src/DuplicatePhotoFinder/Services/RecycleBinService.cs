using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.FileIO;

namespace DuplicatePhotoFinder.Services;

public class RecycleBinService
{
    private readonly ILogger<RecycleBinService> _logger;

    public RecycleBinService(ILogger<RecycleBinService> logger)
    {
        _logger = logger;
    }

    public void SendToRecycleBin(string path)
    {
        _logger.LogInformation("Deleting (recycle bin): {Path}", path);
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }

    public async Task<int> DeleteFilesAsync(
        IEnumerable<string> paths,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        int count = 0;
        await Task.Run(() =>
        {
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(path))
                {
                    _logger.LogWarning("File no longer exists, skipping: {Path}", path);
                    continue;
                }
                try
                {
                    SendToRecycleBin(path);
                    count++;
                    progress?.Report(count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete: {Path}", path);
                }
            }
        }, ct);
        return count;
    }
}
