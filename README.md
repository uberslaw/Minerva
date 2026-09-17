# CpuThrottle

Windows 11 wrapper that launches taxing modelling apps under a **Job Object** with a hard CPU cap (and optional disk / network Tx limits), so the desktop stays usable.

## What it does

1. Creates a Windows Job Object
2. Sets `JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP` (percent × 100)
3. Optionally sets Job Object **I/O rate control** (combined disk bandwidth) and **network rate control** (outbound / Tx)
4. Starts the target process suspended, assigns it to the job, then resumes it
5. Optionally enables Efficiency Mode (EcoQoS), lowers CPU priority, limits affinity, and applies a **best-effort GPU scheduling priority** (WDDM idle / below-normal / normal — not a hard GPU %)

Child processes inherit the job unless they intentionally break away.

## Projects

| Project | Purpose |
| --- | --- |
| `src/CpuThrottle.Core` | Shared Job Object / EcoQoS / affinity / I/O / net helpers |
| `src/CpuThrottle.Cli` | `throttle` CLI |
| `src/CpuThrottle.Tray` | WinForms tray UI (launch, attach from process list, live CPU) |
| `samples/CpuThrottle.SampleBurner` | Multi-thread CPU burner + optional child process tree |
| `tests/CpuThrottle.Tests` | Unit tests + burner process-tree smoke test |

## Build (Windows)

```powershell
.\build-windows.ps1
# or:
dotnet build CpuThrottle.sln -c Release
dotnet test CpuThrottle.sln -c Release
```

On Linux/macOS CI, build the filter that excludes the WinForms tray project:

```bash
dotnet build CpuThrottle.CoreCli.slnf -c Release
dotnet test CpuThrottle.CoreCli.slnf -c Release
```

Publish CLI:

```powershell
dotnet publish src/CpuThrottle.Cli/CpuThrottle.Cli.csproj -c Release -r win-x64 --self-contained false -o .\publish\cli
```

Publish tray UI:

```powershell
dotnet publish src/CpuThrottle.Tray/CpuThrottle.Tray.csproj -c Release -r win-x64 --self-contained false -o .\publish\tray
```

## CLI usage

```powershell
# Cap a modelling app at 50% of machine CPU
.\throttle --cpu 50 -- "C:\Path\To\ModelApp.exe" --input job.dat

# Tighter cap, 4-core affinity, Efficiency Mode on (default)
.\throttle --cpu 35 --cores 4 --priority below-normal -- "C:\Sim\solver.exe"

# Also limit disk I/O hints and outbound network; GPU idle is best-effort only
.\throttle --cpu 40 --disk-read 50M --disk-write 20M --network-tx 5M --gpu idle -- "C:\Sim\solver.exe"

# Attach to an already-running PID (fails if it is already in a conflicting job)
.\throttle --cpu 40 --attach 12345

# Launch sample burner under a 25% cap and watch CPU samples
.\throttle --cpu 25 -- .\CpuThrottle.SampleBurner.exe --seconds 20 --threads 16 --children 2
```

Useful flags:

- `--cpu` / `-c` — hard cap percent (1–100)
- `--cores` / `-n` — affinity to first N logical processors
- `--efficiency` / `--no-efficiency` — EcoQoS toggle
- `--priority` — `idle` | `below-normal` | `normal`
- `--disk-read` / `-dr` — disk read bandwidth hint (`10M`, `512K`, …)
- `--disk-write` / `-dw` — disk write bandwidth hint
- `--network-tx` / `-nt` — network transmit (outbound) bandwidth cap
- `--gpu` / `-g` — `off` | `idle` (alias `low`) | `below-normal` | `normal` (best-effort WDDM GPU scheduling priority; **not** a hard GPU % cap)
- `--wait` / `--no-wait` — wait for exit (default) or keep job alive until Ctrl+C
- `--attach` / `-a` — throttle an existing PID

## Tray UI

Run `CpuThrottle.exe` from the tray publish folder (rebuild after pulling — older builds lack the process list).

- Browse or drag-drop an `.exe`
- Set **CPU hard cap** slider (10–90%)
- Optional affinity cores + Efficiency Mode + CPU priority
- Optional disk read/write KB/s, network Tx KB/s
- **GPU priority** slider: Off → Idle → Below normal → Normal (soft WDDM hint via `D3DKMTSetProcessSchedulingPriorityClass`; **not** a hard GPU utilization %)
- **Running processes** list is on the main window (filter, sort by column, Refresh). Select a row and click **Throttle selected** (or double-click). **Pick process…** opens the larger full picker dialog; the tray menu has the same entry
- **Launch throttled** starts a new process under the job with the current options
- Access-denied / already-in-job attach failures show a clear message (try elevation or another process)
- Stop job; status shows approximate process CPU vs cap
- Minimize to tray; double-click icon to restore

## What is actually enforced vs best-effort

| Resource | Mechanism | Enforcement |
| --- | --- | --- |
| **CPU %** | Job Object `CPU_RATE_CONTROL_HARD_CAP` | Hard cap (Windows 8+) |
| **CPU affinity / priority / EcoQoS** | Job affinity + `SetPriorityClass` + power throttling | Soft / scheduling hints |
| **Disk read / write** | Job Object I/O rate control `MaxBandwidth` | **Combined** hard bandwidth pool for the job (Windows 10+). Separate `--disk-read` / `--disk-write` values are **summed** into one MaxBandwidth; the kernel does **not** expose independent hard read vs write caps on job objects. |
| **Network Tx** | Job Object net rate control `MaxBandwidth` | Hard outbound (transmit) cap for sockets owned by the job (Windows 10+). **Inbound / Rx is not limited.** Not a full WinDivert/WFP packet shaper. |
| **GPU priority** | `D3DKMTSetProcessSchedulingPriorityClass` (`idle` / `below-normal` / `normal`) | **Best-effort only** (scheduling hint). There is no public user-mode API for a hard GPU utilization percent cap on arbitrary processes. Slider/CLI values are priority classes, not GPU %. |

## Validate process-tree throttling (Windows)

```powershell
dotnet build samples/CpuThrottle.SampleBurner -c Release
dotnet build src/CpuThrottle.Cli -c Release

# Burner with children mimics a modelling tree; Task Manager should show the group staying near the cap
.\src\CpuThrottle.Cli\bin\Release\net8.0\throttle.exe --cpu 30 -- `
  .\samples\CpuThrottle.SampleBurner\bin\Release\net8.0\CpuThrottle.SampleBurner.exe `
  --seconds 30 --threads 16 --children 2
```

Without the wrapper, the same burner should peg the machine closer to 100%.

## Linux / CI note

Core, CLI, SampleBurner, and tests compile on non-Windows hosts. The CLI refuses to throttle unless the OS is Windows. The tray project targets `net8.0-windows` and must be built on Windows (or with the Windows Desktop workload targeting pack).

## Requirements

- Windows 8+ for Job Object CPU rate control (developed for Windows 11)
- Windows 10+ for Job Object I/O and network rate control
- .NET 8 SDK
- Elevation only needed when the target process itself requires elevation
