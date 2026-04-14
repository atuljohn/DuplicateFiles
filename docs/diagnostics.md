# Diagnostics & Post-Mortem Playbook

When the app hangs, freezes, or balloons in memory, this playbook tells you
**exactly what to collect** and **exactly what to hand to Claude Code** so it
can investigate without guessing.

---

## What's running automatically

Every time the app starts, `DiagnosticsService` writes a heartbeat to:

```
%LOCALAPPDATA%\DuplicatePhotoFinder\diagnostics\heartbeat-YYYYMMDD.jsonl
```

Each line is a JSON object with:

| Field              | Meaning                                                  |
|--------------------|----------------------------------------------------------|
| `ts`               | UTC timestamp (ISO 8601)                                 |
| `kind`             | `Startup` / `Heartbeat` / `Snapshot:*` / `HANG_DETECTED` / `Shutdown` |
| `phase`            | Current phase string (e.g. "Computing perceptual hashes...") |
| `filesProcessed`   | Files processed so far                                   |
| `totalFiles`       | Total files in the current phase                         |
| `groupsFound`      | Duplicate groups discovered                              |
| `isBusy`           | True while a scan or sync is running                     |
| `managedMb`        | `GC.GetTotalMemory` in MB                                |
| `workingSetMb`     | OS-reported working set                                  |
| `privateMb`        | Private bytes (best signal for native + pooled memory)   |
| `gen0`/`gen1`/`gen2` | Cumulative GC collection counts                        |
| `threadCount`      | Process thread count                                     |
| `workerThreadsBusy`| Thread pool worker threads in use                        |
| `ioThreadsBusy`    | Thread pool I/O threads in use                           |

**Hang detection:** if `isBusy` is true and neither `phase` nor `filesProcessed`
changes for 60 seconds, an entry with `kind: "HANG_DETECTED"` is appended,
including a ready-to-run `dumpHint` command line.

---

## When a hang happens

### Step 1 — DO NOT close the app yet

The app may still be alive (just blocked / GC-thrashing). You need a process
dump while the bad state is still in memory.

### Step 2 — Capture a process dump

Open a NEW PowerShell window (don't kill the hung app first):

```powershell
# Install the dump tool once (no-op if already installed)
dotnet tool install -g dotnet-dump

# Find the hung process
Get-Process DuplicatePhotoFinder

# Capture a full dump (replace <PID> with the number from above)
dotnet-dump collect -p <PID> `
  -o "$env:LOCALAPPDATA\DuplicatePhotoFinder\diagnostics\hang-$(Get-Date -Format yyyyMMdd-HHmmss).dmp"
```

If `HANG_DETECTED` was already logged, copy/paste the `dumpHint` command from
the latest `heartbeat-*.jsonl` line — it has the exact PID and output path
pre-filled.

### Step 3 — Now you can kill the app

```powershell
Stop-Process -Name DuplicatePhotoFinder -Force
```

### Step 4 — Extract diagnostic data from the dump

```powershell
dotnet-dump analyze "$env:LOCALAPPDATA\DuplicatePhotoFinder\diagnostics\hang-*.dmp"
```

Inside the analyzer prompt, run these commands and **save the output to a text file**:

```
> dumpheap -stat
> dumpheap -type SixLabors -stat
> dumpheap -type System.Windows.Media.Imaging -stat
> dumpheap -type System.Threading.Tasks.Task -stat
> threads
> clrstack -all
> eeheap -gc
> exit
```

Save it to `diagnostics\hang-<date>.dump-analysis.txt`.

---

## Step 5 — Hand it to Claude Code

Open Claude Code in the project root and paste **one of these prompts** (adjust
the date):

### Quick triage (heartbeat only, no dump needed)

```
The app hung. Read the latest heartbeat log at
%LOCALAPPDATA%\DuplicatePhotoFinder\diagnostics\heartbeat-YYYYMMDD.jsonl
and tell me:
1. The last successful phase + file count
2. Memory growth pattern over the 5 minutes leading up to the hang
3. Whether gen2 GC counts spiked (sign of GC thrash)
4. Your best guess at the root cause
Then propose a concrete fix.
```

### Full post-mortem (with dump analysis output)

```
The app hung. I have:
- Heartbeat log: %LOCALAPPDATA%\DuplicatePhotoFinder\diagnostics\heartbeat-YYYYMMDD.jsonl
- Dump analysis: %LOCALAPPDATA%\DuplicatePhotoFinder\diagnostics\hang-YYYYMMDD-HHMMSS.dump-analysis.txt

Read both, then:
1. Identify what the threads were blocked on (clrstack output)
2. Identify the top heap consumers (dumpheap -stat output)
3. Correlate against the heartbeat phase at the time of hang
4. Tell me what to fix, with file:line references
```

Claude can read JSONL files directly with the Read tool and parse the dump
analysis text. The dump file itself (`*.dmp`) is binary and not directly
readable, but Claude will guide you through any extra `dotnet-dump analyze`
commands needed.

---

## What to look for in the heartbeat (manually)

Open the JSONL in any editor. Key red flags:

| Pattern                                            | Likely cause                                |
|----------------------------------------------------|---------------------------------------------|
| `managedMb` stable, `privateMb` rising fast        | Native / pooled buffer leak (ImageSharp pool) |
| `managedMb` rising, `gen2` rising rapidly          | Object retention in managed heap (collections holding references) |
| `workerThreadsBusy` near max + no `filesProcessed` change | Thread pool starvation (deadlock or sync-over-async) |
| `phase` changes but `filesProcessed` stuck at 0    | Phase init is hanging (e.g. directory enumeration) |
| `ioThreadsBusy` high                               | I/O contention (network share? slow disk?) |
| Last entry is `HANG_DETECTED`                      | Watchdog fired; check `lastPhase` field     |

---

## Manually triggering a snapshot

If you suspect a problem but the watchdog hasn't fired, you can grab a dump on
demand using the same PowerShell snippet from Step 2 — no need to wait.

---

## Reset / rotate logs

Heartbeat files are date-stamped (`heartbeat-YYYYMMDD.jsonl`) so they self-rotate
daily. Old files don't auto-delete; clean them up periodically:

```powershell
Get-ChildItem "$env:LOCALAPPDATA\DuplicatePhotoFinder\diagnostics" -Filter "heartbeat-*.jsonl" |
  Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-14) } |
  Remove-Item
```
