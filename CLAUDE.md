# Duplicate Photo & Video Finder

Native Windows WPF desktop application for finding and removing duplicate photos and videos across large media libraries (~400GB+).

## Tech Stack

- **Language:** C# / .NET 8
- **UI:** WPF (Windows Presentation Foundation)
- **Image processing:** SixLabors.ImageSharp 3.1.7
- **Video processing:** FFMpegCore 5.1.0 (requires ffmpeg/ffprobe binaries in `tools/`)
- **MVVM:** CommunityToolkit.Mvvm 8.3.2
- **DI:** Microsoft.Extensions.DependencyInjection
- **Logging:** Serilog → `%LOCALAPPDATA%\DuplicatePhotoFinder\deletions.log`

## Project Structure

```
src/DuplicatePhotoFinder/
├── Models/
│   ├── MediaFile.cs          — single scanned file with hash/metadata fields
│   ├── DuplicateGroup.cs     — set of duplicate files with recommended keep
│   └── ScanOptions.cs        — user scan configuration
├── Services/
│   ├── MediaScanner.cs             — directory traversal, EXIF metadata
│   ├── CryptographicHashService.cs — SHA-256 exact match
│   ├── PerceptualHashService.cs    — custom dHash (no external dependency)
│   ├── VideoFingerprintService.cs  — FFmpeg frame sampling + dHash
│   ├── ThumbnailService.cs         — Shell thumbnails with ImageSharp fallback
│   ├── DuplicateDetector.cs        — orchestrates full pipeline, IAsyncEnumerable
│   ├── AutoSelectionService.cs     — scoring logic (keep best file)
│   └── RecycleBinService.cs        — safe deletion via Recycle Bin
├── ViewModels/
│   ├── MainViewModel.cs            — CurrentView string drives navigation
│   ├── ScanProgressViewModel.cs
│   ├── DuplicateGroupViewModel.cs
│   └── MediaFileViewModel.cs
├── Views/
│   ├── ScanConfigView.xaml         — folder picker + detection options
│   ├── ScanProgressView.xaml       — live progress bar + stats
│   ├── DuplicateReviewView.xaml    — virtualized list of duplicate groups
│   └── DuplicateGroupPanel.xaml   — side-by-side thumbnail cards per group
├── Converters/
│   └── StringToVisibilityConverter.cs  — and other WPF converters
├── GlobalUsings.cs                 — global using System.IO (required for WPF _wpftmp.csproj)
└── tools/
    ├── ffmpeg.exe   ← must be placed here manually
    └── ffprobe.exe  ← must be placed here manually
```

## Detection Pipeline

1. **Phase 1 — Exact match:** Group by `(file size, SHA-256)`. Fastest, handles byte-for-byte copies.
2. **Phase 2 — Perceptual hash (images):** Custom dHash via ImageSharp. Hamming distance ≤ threshold (default 10) = similar image. Handles resized/recompressed duplicates.
3. **Phase 3 — Video fingerprint:** Extract N frames via FFMpeg, dHash each, compare average Hamming distance.

Groups are yielded via `IAsyncEnumerable<DuplicateGroup>` so the UI updates live during scanning.

## Auto-Selection Scoring

`AutoSelectionService.ScoreFile()` — higher = keep:
- +pixels (resolution)
- +MB (file size)
- +1000 for paths containing user-specified preferred folder keywords
- −500 for paths containing "backup", "temp", "copy", "duplicate"
- +200 for files with EXIF DateTaken
- −10 per path depth level

## Navigation

`MainViewModel.CurrentView` (string) drives view visibility in `MainWindow.xaml`:
- `"Config"` → ScanConfigView
- `"Progress"` → ScanProgressView
- `"Review"` → DuplicateReviewView

## Build & Run

```bash
# Build
dotnet build src/DuplicatePhotoFinder/DuplicatePhotoFinder.csproj

# Run
dotnet run --project src/DuplicatePhotoFinder/

# Publish as single self-contained exe
dotnet publish src/DuplicatePhotoFinder/ -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Known Constraints

- **NuGet SSL issues:** The machine has SSL connectivity problems with nuget.org. Only packages already in the local cache (`~/.nuget/packages/`) can be used. `CoenM.ImageHash` was not available — dHash is implemented directly in `PerceptualHashService.cs`.
- **GlobalUsings.cs:** Must contain `global using System.IO;`. WPF's XAML compilation creates a `_wpftmp.csproj` that does not inherit `ImplicitUsings=enable`, so System.IO types would be missing without this file.
- **HEIC support:** LibHeifSharp was planned but not added (not in NuGet cache). HEIC files will attempt to decode via ImageSharp, which has limited HEIC support. Shell thumbnails may work if the HEVC codec pack is installed.
- **FFmpeg binaries:** Must be placed in `src/DuplicatePhotoFinder/tools/ffmpeg.exe` and `ffprobe.exe`. Without them, video similarity detection is skipped (exact hash still works for videos).

## Safety

- Deletions use `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile` with `RecycleOption.SendToRecycleBin` — files go to Recycle Bin, not permanently deleted.
- Every deletion is logged to `%LOCALAPPDATA%\DuplicatePhotoFinder\deletions.log` before execution.
