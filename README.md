# T4Power

Power and clock manager for NVIDIA GPUs on Windows, built for the **Tesla T4**.

Set the power envelope by hand, or let it follow what's actually running — with GPU discovery
by UUID, a tray UI, a scriptable CLI, and a service that applies your profile at boot before
anyone logs in.

## Why clocks, not just watts

The obvious design is a power-limit slider. On a T4 that alone does almost nothing, and it's
worth being clear about why.

Measured on a Tesla T4 (driver 610.74, MCDM):

| State | Power limit | SM clock | Draw | Temp | P-state |
|---|---|---|---|---|---|
| Idle, as found | 70 W | 1590 MHz | **36.0 W** | **63 °C** | P0 |
| Idle, `-pl 65` | 65 W | 1590 MHz | 39.1 W | 63 °C | P0 |
| Idle, clocks locked 300–900 MHz | 65 W | 300 MHz | **~10 W** | **53 °C** | P8 |

Two things follow:

- The T4's power limit is adjustable only across **60–70 W**, and at idle the card draws 36 W —
  *below* the 60 W floor. So lowering the cap cannot reduce idle draw at all. It only bounds
  sustained load.
- The card was sitting wedged at **P0/1590 MHz with no compute process running**. Locking the
  SM clock is what actually drops idle power (**−72%**) and temperature (**−11 °C**).

So a profile in T4Power sets **both** knobs: a power limit for sustained load, and a clock lock
for everything else. A lock→unlock cycle also knocks the card out of a stuck P0, which the
service does at startup when it detects that state.

## How it's put together

One executable, several modes:

| Invocation | Runs as | Purpose |
|---|---|---|
| `T4Power` | you | Tray UI |
| `T4Power --service` | LocalSystem | The service body — the only thing that writes to NVML |
| `T4Power --install-service` | admin, once | Copies to Program Files and registers the service |
| `T4Power --list` / `--status` / `--set` / … | you | CLI |

```
T4Power.exe --service  [LocalSystem]          T4Power.exe  [your session]
  NVML session, 1 s poll loop                   tray UI / CLI
  rule engine, config writer  <-- \\.\pipe\T4Power -->  sliders, presets, scripts
```

**Nothing you use day to day needs elevation.** Setting a power limit does require admin
rights, but the privilege boundary is the *pipe ACL*, not the caller's token: the LocalSystem
service performs the write, and a client only needs permission to ask. `--install-service`
records the installing user's SID, and that plus `BUILTIN\Administrators` is the whole guest
list — deliberately not `BUILTIN\Users`, which would let any account on the machine change GPU
power. Every request is re-validated and clamped against live NVML constraints server-side.

## Automation

The CLI is a thin pipe client, so scripts, hotkeys, and coding agents drive it unelevated:

```powershell
T4Power --list --json                     # UUIDs, ranges, capabilities
T4Power --status --json                   # live draw / clock / temp / profile / active rule
T4Power --profile Max --gpu T4 --for 30m  # temporary boost, auto-reverts
T4Power --set --gpu T4 --power 70 --clocks unlock --for 45m
T4Power --auto --gpu T4                   # drop the override, back to the rules

T4Power --rules --gpu T4                  # profiles and auto-switch rules
T4Power --watch ollama.exe blender.exe    # ramp up while these run
T4Power --unwatch ollama.exe
```

`--for` is enforced by the service on its own tick, so an override still expires if whatever
set it crashed or walked away. Exit codes distinguish *service down* (2) from *unknown GPU* (3)
from *out of range* (4), so failures are actionable without parsing prose.

## Profiles and rules

Config lives at `C:\ProgramData\T4Power\config.json`, keyed by GPU UUID — never by index, which
is not stable across reboots. The service is the sole writer.

Defaults for a T4:

| Profile | Power | Clocks |
|---|---|---|
| Eco | 60 W | locked 300–900 MHz |
| Balanced | 70 W | locked 300–1290 MHz |
| Max | 70 W | unlocked |

Rules pick a profile automatically, highest priority first:

1. **Process name** — a named executable is running (ramps up *before* it touches the GPU).
2. **GPU activity** — anything holds a compute/graphics context, or utilisation is above a
   threshold. App-agnostic, so it catches workloads nobody listed.
3. **Idle** — the catch-all.

Ramp-up is immediate; ramp-down waits out a dwell period. That hysteresis is what stops the
profile flapping between Eco and Max in the gaps between kernels.

Other GPUs are discovered and reported but **not managed by default** — clamping someone's
display adapter just because it enumerated would be a nasty surprise.

## Building

Needs the .NET 10 SDK.

```powershell
dotnet build                                  # or: dotnet test
dotnet run --project src/T4Power -- --list
```

Release build — one self-contained 72 MB exe with no runtime prerequisite:

```powershell
dotnet publish src/T4Power -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o publish

.\publish\T4Power.exe --install-service    # one UAC prompt; copies to Program Files
```

During development, run `T4Power --service` in the foreground instead of installing — an
installed service holds a lock on its own binaries.

## Safety

- Every write is clamped to the range NVML reports for that specific GPU.
- Stopping or uninstalling the service restores the default power limit and releases clock
  locks, so the machine is never left clamped by software that isn't running.
- A thermal guard forces Eco above a configurable temperature, outranking even a manual
  override — the T4 is passively cooled and depends entirely on chassis airflow.
- Logs at `C:\ProgramData\T4Power\logs\`.
