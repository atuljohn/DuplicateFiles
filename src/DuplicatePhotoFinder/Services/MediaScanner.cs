using DuplicatePhotoFinder.Models;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace DuplicatePhotoFinder.Services;

public class MediaScanner
{
    private readonly ILogger<MediaScanner> _logger;

    public MediaScanner(ILogger<MediaScanner> logger)
    {
        _logger = logger;
    }

    public async IAsyncEnumerable<MediaFile> ScanAsync(ScanOptions options,
        IProgress<string>? progress = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var files = Directory.EnumerateFiles(options.RootFolder, "*", SearchOption.AllDirectories);

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(path);
            if (!options.IsMediaFile(ext)) continue;

            progress?.Report(path);

            MediaFile? file = null;
            try
            {
                file = await BuildMediaFileAsync(path, options, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read file metadata: {Path}", path);
            }

            if (file != null)
                yield return file;
        }
    }

    private async Task<MediaFile> BuildMediaFileAsync(string path, ScanOptions options, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var ext = Path.GetExtension(path);
        var kind = options.ImageExtensions.Contains(ext) ? MediaKind.Image : MediaKind.Video;

        DateTime? dateTaken = null;
        int? width = null, height = null;

        if (kind == MediaKind.Image && !IsHeic(ext))
        {
            try
            {
                var imgInfo = await Image.IdentifyAsync(path, ct);
                if (imgInfo != null)
                {
                    width = imgInfo.Width;
                    height = imgInfo.Height;
                    var exif = imgInfo.Metadata.ExifProfile;
                    if (exif != null)
                    {
                        if (exif.TryGetValue(ExifTag.DateTimeOriginal, out var dt) && dt?.Value != null)
                            dateTaken = ParseExifDate(dt.Value);
                        else if (exif.TryGetValue(ExifTag.DateTime, out var dt2) && dt2?.Value != null)
                            dateTaken = ParseExifDate(dt2.Value);
                    }
                }
            }
            catch { /* non-fatal */ }
        }

        return new MediaFile
        {
            FullPath = path,
            FileSizeBytes = info.Length,
            DateModified = info.LastWriteTime,
            DateTaken = dateTaken,
            WidthPixels = width,
            HeightPixels = height,
            Kind = kind
        };
    }

    private static bool IsHeic(string ext) =>
        ext.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".heif", StringComparison.OrdinalIgnoreCase);

    private static DateTime? ParseExifDate(string value)
    {
        if (DateTime.TryParseExact(value, "yyyy:MM:dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt))
            return dt;
        return null;
    }
}
