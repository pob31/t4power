# T4Power

Power and clock manager for NVIDIA GPUs on Windows, built for the **Tesla T4**.

Hold the card at full clocks while a demanding app runs, drop it back when nothing needs it,
or set it by hand — with GPU discovery by UUID, a tray UI, a scriptable CLI, and a service that
applies your profile at boot before anyone logs in.

---

## The one thing worth knowing

The obvious design is a power-limit slider. On a T4 that alone does almost nothing, and the
reason cuts both ways.

Measured on a Tesla T4 (driver 610.74, MCDM):

| Clock state | SM clock | P-state | Draw | Temp |
|---|---|---|---|---|
| Found idling, wedged | 1590 MHz | P0 | 36 W | 63 °C |
| Unlocked, idle | 300 MHz | P8 | ~10 W | 52 °C |
| Pinned 300–900 MHz | 300 MHz | P8 | ~10 W | 52 °C |
| **Pinned 1590 MHz** | **1590 MHz** | **P0** | **34 W** | 58 °C |

Two conclusions:

- **The power limit is not the lever.** The T4's limit is adjustable only across **60–70 W**,
  and at idle the card draws 36 W — *below* its own floor. Lowering the cap cannot reduce idle
  draw at all. It only bounds sustained load.
- **"Unlocked" does not mean fast.** It means *let the card decide*, and an idle card decides
  P8/300 MHz. To force P0 you must pin the clock **floor** high. Equivalently:
  `nvidia-smi -lgc 1590`.

So a profile in T4Power sets **both** knobs, and the clock lock is a *pin* rather than a
ceiling — pin high to hold P0, pin low to cut idle draw and heat.

A lock→unlock cycle also knocks the card out of a stuck P0, which the service does at startup
when it detects that state.

### Why pinning matters more than power saving

This was written for a real-time audio application doing its DSP on the T4. There, a card that
idles to P8 between buffers and ramps back up on each burst is not merely slower — the
transition shows up as an **xrun**. A pinned clock is a stable one, and stability is the point.

---

## How it's put together

One executable, several modes:

| Invocation | Runs as | Purpose |
|---|---|---|
| `T4Power` | you | Tray icon |
| `T4Power --window` | you | Tray icon with the window open |
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
power. Every request is re-validated and clamped against live NVML constraints server-side,
because anything arriving on the pipe is untrusted.

---

## Profiles and rules

Config lives at `C:\ProgramData\T4Power\config.json`, keyed by GPU UUID — never by index, which
is not stable across reboots. The service is the sole writer.

Defaults for a T4, forming a forced-low / automatic / forced-high scale:

| Profile | Power | Clocks | Effect |
|---|---|---|---|
| **Eco** | 60 W | pinned 300–900 MHz | ~10 W idle, coolest |
| **Balanced** | 70 W | unlocked | card manages itself |
| **Max** | 70 W | **pinned 1590 MHz** | held in P0, full performance |

Rules pick a profile automatically, highest priority first:

1. **Process name** — a named executable is running. Matched by name, so it is found wherever
   it lives, a build output or Program Files alike. Ramps up *before* the app touches the GPU.
2. **GPU activity** — anything holds a compute/graphics context, or utilisation is above a
   threshold. App-agnostic, so it catches workloads nobody listed.
3. **Idle** — the catch-all.

Ramp-up is immediate; ramp-down waits out a dwell period. That hysteresis is what stops the
profile flapping between Eco and Max in the gaps between kernels, and `--status` tells you when
a rule is merely holding:

```
profile: Max   (while any of [WFS-DIY] is running -> Max (holds 60s after it exits)
                - holding for 15s more)
```

Other GPUs are discovered and reported but **not managed by default** — clamping someone's
display adapter just because it enumerated would be a nasty surprise.

---

## Motherboard fan control

The T4 is passively cooled. Fit an aftermarket cooler and its fan lands on a motherboard header,
which means the thing keeping the card alive is controlled by something entirely separate from
the thing managing the card. The usual fix is a second app plus a script that shuttles the GPU
temperature across — which is exactly as fragile as it sounds, since the relay can stall, race,
or feed a stale reading to a fan curve.

T4Power reads that temperature in-process, on the same tick it already uses for the rule engine,
and drives the header directly. One app, no relay.

```powershell
T4Power --fans                                  # every header, with live RPM and duty
T4Power --identify-fan control/3 --for 10s      # spin one up so you can hear which it is
T4Power --adopt-fan control/3 --gpu T4          # drive it from the T4's temperature
T4Power --fan-curve control/3                   # show the curve
T4Power --fan-curve control/3 --points "49.5:27,62.8:100"
T4Power --fan-set control/3 --percent 60 --for 10m
T4Power --fan-auto control/3                    # drop the manual duty, back to the curve
T4Power --release-fan control/3                 # hand it back to the BIOS, stop managing it
```

The window has the same thing as a draggable curve, with a live marker showing where the GPU is.

**No header is ever driven until you explicitly adopt one.** Adoption commands the header to full
and checks the tachometer responds before committing, because the failure mode of a mistyped
selector is quietly taking over the CPU pump.

### Prerequisite: PawnIO

Reaching a SuperIO chip means talking to legacy I/O ports, which needs a kernel driver. The
traditional one, WinRing0, **cannot load when Memory Integrity (HVCI) is enabled** — it is on
Microsoft's vulnerable-driver blocklist. LibreHardwareMonitor moved to
[PawnIO](https://github.com/namazso/PawnIO.Setup/releases) for that reason, and so this needs it
too. Install it once; T4Power starts the driver itself on each boot, since its start type is
manual and nothing else will.

Without PawnIO everything else works exactly as before — fan control is strictly additive, and
the app says so rather than failing obscurely.

### Set the BIOS curve for that header to Full Speed

This is a safety requirement, not a tip.

Whenever T4Power hands a header back — service stop, uninstall, lost GPU telemetry — the header
reverts to what the BIOS programmed at POST. That fallback is only as safe as the BIOS curve
behind it. Setting the header to Full Speed makes every hand-back unconditionally safe, and makes
POST, a crash and a power cut safe for free, because the SuperIO is reinitialised from BIOS
settings on every reset.

The worst case then is a loud fan, rather than a cooked card.

### How the curve behaves

Duty is piecewise-linear between points and flat beyond the ends. Two independent mechanisms stop
it chasing noise: a **hysteresis band** decides whether a reading counts as a change at all, and a
**response time** decides how long that change must persist before it is acted on. Both are
asymmetric by default — ramp up in 3 s, ramp down over 30 s — because heat is already happening
when you see it, while a fan that chases every dip audibly hunts.

Above a **panic temperature** (80 °C by default, below the thermal guard's 85 °C) the fan goes to
full immediately, bypassing the curve, the smoothing *and* any manual override. Trying to move
more air comes before clamping the card's clocks.

If the GPU's temperature goes missing or stale, the header is handed back to the BIOS rather than
driven on a guess.

---

## Automation

The CLI is a thin pipe client, so scripts, hotkeys and coding agents drive it unelevated:

```powershell
T4Power --list --json                     # UUIDs, ranges, capabilities
T4Power --status --json                   # live draw / clock / temp / profile / active rule
T4Power --profile Max --gpu T4 --for 30m  # temporary boost, auto-reverts
T4Power --set --gpu T4 --power 70 --clocks 1590   # pin to P0 by hand
T4Power --auto --gpu T4                   # drop the override, back to the rules

T4Power --rules --gpu T4                  # profiles and auto-switch rules
T4Power --watch WFS-DIY.exe               # hold Max while this runs
T4Power --unwatch WFS-DIY.exe
```

`--clocks` takes a single value to pin (`1590`), a range (`300-900`), or `unlock`.

`--for` is enforced by the service on its own tick, so an override still expires if whatever
set it crashed or walked away. Exit codes distinguish *service down* (2) from *unknown GPU* (3)
from *out of range* (4), so failures are actionable without parsing prose. `--json` on every
read verb makes the output a stable contract.

---

## Installing

Needs the .NET 10 SDK to build; the published binary needs nothing.

```powershell
dotnet publish src/T4Power -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o publish

.\publish\T4Power.exe --install-service    # one UAC prompt; copies to Program Files
```

One self-contained exe, no runtime prerequisite. Uninstall with `--uninstall-service`, which
stops the service, restores the default power limit, releases the clock lock and hands any
adopted fan header back to the BIOS.

Do **not** add `-p:PublishTrimmed=true`: LibreHardwareMonitor is reflection-heavy and would fail
at runtime rather than at build time. After changing anything in the fan layer, test the
*published single-file exe* and not just `dotnet run` — single-file is a different assembly load
context, and `Assembly.Location` returns `""` inside a bundle.

During development run `T4Power --service` in the foreground instead of installing — an
installed service holds a lock on its own binaries. If an install fails with *access denied*
while clearing the install directory, a T4Power process is still running; Windows will not
delete a running image.

```powershell
dotnet build
dotnet test
dotnet run --project tools/IconGen        # regenerate the application icon
```

---

## Safety

- Every write is clamped to the range NVML reports for that specific GPU; out-of-range requests
  are clamped and the response says so.
- Stopping or uninstalling the service restores the default power limit and releases clock
  locks, so the machine is never left clamped by software that isn't running.
- A **thermal guard** forces Eco above a configurable temperature (85 °C by default on the T4),
  outranking even a manual override — the card is passively cooled and depends entirely on
  chassis airflow. Note that for a latency-sensitive workload this is itself a clock transition;
  set `thermalGuardC` to `null` in `config.json` to disable it.
- Logs at `C:\ProgramData\T4Power\logs\`, including install failures, which are otherwise
  invisible because the elevated installer gets its own console.

### Fan control, honestly

Fan headers deserve their own note, because a fan stuck at the wrong speed is worse than a GPU
stuck at the wrong clock.

| What happens | The header ends up | Covered by |
|---|---|---|
| Graceful stop, uninstall, OS shutdown | BIOS curve | code, deterministic |
| Unhandled crash or `taskkill /F` | **holds its last duty** | the SCM restart actions (5 s) bring the service back and it resumes; the panic rule means a stale duty is a high one whenever the card was hot |
| Service crashes on every start | BIOS curve | a header is only taken over once a *fresh* GPU temperature has been read |
| BSOD, power cut, reset | BIOS curve | hardware — the SuperIO is reinitialised at POST |
| Sleep/resume, or another app grabs the channel | reclaimed within a second | the control mode is read back each tick and rewritten if it is not ours |
| GPU removed, driver reload, NVML failure | BIOS curve after 10 s | the sensor-timeout fail-safe |
| PawnIO removed while a header is held | **holds its last duty until POST** | nothing — this one has no software remedy |

The last row is the reason the BIOS Full Speed setting above is a requirement rather than advice.

Commanded duty is clamped to what the chip reports it accepts before it is written, on the same
principle as power limits: values arrive over the pipe and are treated as untrusted. A duty floor
(20 % by default) keeps a blower above its stall threshold, since a fan reading 0 RPM is worse
than a slow one.

---

## Third-party components

- **[LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)** —
  motherboard sensor and fan control, used unmodified. Licensed under the
  **Mozilla Public License 2.0**, which is compatible with the GPL by way of its section 3.3.
  Source for the covered files is available at the link above.
- **[PawnIO](https://github.com/namazso/PawnIO.Setup)** — the kernel driver LibreHardwareMonitor
  uses for port I/O. Installed separately by the user; not distributed with T4Power.
- LibreHardwareMonitorLib's own dependencies (HidSharp, DiskInfoToolkit, RAMSPDToolkit,
  Mono.Posix.NETStandard, System.IO.Ports, System.Management) ship in the published binary under
  their respective MIT/Apache/MPL licences.

---

## License

Copyright (C) 2026 Pierre-Olivier Boulant.

T4Power is free software: you may redistribute it and/or modify it under the terms of the
**GNU General Public License, version 3 or later**, as published by the Free Software
Foundation. It is distributed in the hope that it will be useful, but **without any warranty** —
without even the implied warranty of merchantability or fitness for a particular purpose. See
the [LICENSE](LICENSE) file, or <https://www.gnu.org/licenses/>, for the full text.
