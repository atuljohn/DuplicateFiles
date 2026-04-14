# Duplicate Photo & Video Finder + Folder Sync

Native Windows WPF desktop app with two tools:
- **Duplicate Finder** — scan a media library for exact and visually similar duplicates
- **Folder Sync** — One-Way / Mirror / Two-Way sync between two folders

Built on .NET 8, ImageSharp, FFMpegCore. See `CLAUDE.md` for architecture and
`docs/diagnostics.md` for the full diagnostic playbook.

---

## 🚨 When the app hangs or eats your RAM — read this

The app writes a heartbeat log every 5 seconds to:

```
%LOCALAPPDATA%\DuplicatePhotoFinder\diagnostics\heartbeat-YYYYMMDD.jsonl
```

Each line records phase, files processed, managed/working/private memory, GC
counts, and thread pool stats. A watchdog flags no-progress hangs (60s+) and
logs a ready-to-run `dotnet-dump` command line.

### Step 1 — Capture a process dump (BEFORE killing the app)

```powershell
# Install the tool once
dotnet tool install -g dotnet-dump

# Find the PID
Get-Process DuplicatePhotoFinder

# Capture the dump
dotnet-dump collect -p <PID> `
  -o "$env:LOCALAPPDATA\DuplicatePhotoFinder\diagnostics\hang-$(Get-Date -Format yyyyMMdd-HHmmss).dmp"
```

If `HANG_DETECTED` already fired, the latest line in `heartbeat-*.jsonl` has a
`dumpHint` field with the exact command pre-filled — copy and run it.

### Step 2 — Now you can kill the app

```powershell
Stop-Process -Name DuplicatePhotoFinder -Force
```

### Step 3 — Open Claude Code in this repo and paste ONE of these prompts

**Quick triage (heartbeat only — no dump needed):**

```
The app hung. Read the latest heartbeat log at
%LOCALAPPDATA%\DuplicatePhotoFinder\diagnostics\heartbeat-YYYYMMDD.jsonl
and tell me:
1. The last successful phase + file count
2. Memory growth pattern in the 5 minutes leading up to the hang
3. Whether gen2 GC counts spiked (sign of GC thrash)
4. Your best guess at the root cause
Then propose a concrete fix.
```

**Full post-mortem (with dump analysis):**

First extract dump data into a text file:

```powershell
dotnet-dump analyze "$env:LOCALAPPDATA\DuplicatePhotoFinder\diagnostics\hang-*.dmp"
# Inside the analyzer, run and pipe to a file:
> dumpheap -stat
> dumpheap -type SixLabors -stat
> dumpheap -type System.Threading.Tasks.Task -stat
> threads
> clrstack -all
> exit
```

Save the output to `hang-YYYYMMDD-HHMMSS.dump-analysis.txt` in the diagnostics
folder. Then in Claude Code:

```
The app hung. I have:
- Heartbeat log: %LOCALAPPDATA%\DuplicatePhotoFinder\diagnostics\heartbeat-YYYYMMDD.jsonl
- Dump analysis: %LOCALAPPDATA%\DuplicatePhotoFinder\diagnostics\hang-YYYYMMDD-HHMMSS.dump-analysis.txt

Read both, then:
1. Identify what threads were blocked on (clrstack output)
2. Identify the top heap consumers (dumpheap -stat output)
3. Correlate against the heartbeat phase at the time of hang
4. Tell me what to fix, with file:line references
```

Claude can read JSONL and the analysis text file directly. Full playbook with
red-flag patterns to look for is in `docs/diagnostics.md`.

---

## Build & Run

```bash
# Build
dotnet build src/DuplicatePhotoFinder/DuplicatePhotoFinder.csproj

# Run
dotnet run --project src/DuplicatePhotoFinder/

# Publish as single self-contained exe
dotnet publish src/DuplicatePhotoFinder/ -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

FFmpeg binaries (`ffmpeg.exe`, `ffprobe.exe`) must be placed in
`src/DuplicatePhotoFinder/tools/` for video similarity detection.
