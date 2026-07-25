using LibreHardwareMonitor.Hardware;
using T4Power.Core;
using T4Power.Core.Fans;

namespace T4Power.Service;

/// <summary>
/// The real fan layer: LibreHardwareMonitor talking to the board's SuperIO chip through PawnIO.
///
/// This is the only file in the repository that references the library, which is the point of
/// <see cref="IFanHardware"/> — the decision-making in T4Power.Core stays free of it, and so do
/// the tests.
/// </summary>
public sealed class LhmFanHardware : IFanHardware
{
    /// <summary>
    /// The canonical LibreHardwareMonitor update pattern. Sub-hardware has to be visited
    /// explicitly: the SuperIO chip hangs off the motherboard as sub-hardware, so a plain
    /// <c>hardware.Update()</c> over the top level refreshes nothing we care about.
    /// </summary>
    sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    readonly FileLog _log;
    readonly UpdateVisitor _visitor = new();

    readonly Dictionary<string, ISensor> _controls = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ISensor> _tachometers = new(StringComparer.OrdinalIgnoreCase);

    Computer? _computer;
    List<FanChannel> _channels = [];

    LhmFanHardware(FileLog log, Computer? computer, string? unavailableReason)
    {
        _log = log;
        _computer = computer;
        UnavailableReason = unavailableReason;

        if (computer is null) return;

        // One update pass BEFORE enumerating, and it is load-bearing.
        //
        // The library activates sensors lazily: a sensor only joins IHardware.Sensors once it has
        // produced a reading. Control channels are created eagerly because they carry an IControl,
        // but tachometers are not, so enumerating straight after Open() finds every fan header and
        // not one of the RPM sensors that pair with them. That silently costs the RPM readout and,
        // worse, the response check at adoption - which is the guard against driving the wrong fan.
        try
        {
            Refresh();
        }
        catch (FanHardwareException ex)
        {
            _log.Warn($"the first fan sensor refresh failed: {ex.Message}");
        }

        Discover();
    }

    public bool IsAvailable => _computer is not null && _channels.Count > 0;
    public string? UnavailableReason { get; private set; }
    public IReadOnlyList<FanChannel> Channels => _channels;

    /// <summary>
    /// Opens the hardware, or returns an instance that politely reports why it could not.
    ///
    /// Never throws. Fan control is additive: a machine with no PawnIO, no supported chip, or a
    /// motherboard the library does not recognise must still get full GPU management.
    /// </summary>
    public static LhmFanHardware TryOpen(FileLog log)
    {
        var pawnIo = PawnIoProbe.EnsureRunning(log);
        if (pawnIo is not null)
        {
            log.Warn($"fan control unavailable: {pawnIo}");
            return new LhmFanHardware(log, computer: null, pawnIo);
        }

        try
        {
            var computer = new Computer
            {
                // The SuperIO chip lives under the motherboard, and that is all we want.
                IsMotherboardEnabled = true,

                // Everything else off, and two of them for concrete reasons rather than tidiness:
                // IsGpuEnabled would have the library initialise NVML/NVAPI inside a process that
                // already owns an NvmlSession, which is how you get "Uninitialized" races at
                // shutdown; IsCpuEnabled would load PawnIO's MSR modules for readings nobody asked
                // for. IsMemoryEnabled probes the SMBus, which is not free either.
                IsCpuEnabled = false,
                IsGpuEnabled = false,
                IsMemoryEnabled = false,
                IsStorageEnabled = false,
                IsControllerEnabled = false,
                IsNetworkEnabled = false,
                IsPsuEnabled = false,
                IsBatteryEnabled = false,
            };

            computer.Open();

            var hardware = new LhmFanHardware(log, computer, unavailableReason: null);

            if (hardware.IsAvailable)
            {
                log.Info($"fan control ready: {hardware._channels.Count} header(s) on " +
                         $"{string.Join(", ", hardware._channels.Select(c => c.ChipName).Distinct())}");
            }
            else
            {
                // Opening succeeded but nothing writable turned up. On a board with a supported
                // chip that almost always means the driver is not actually reaching the ports, so
                // point at the likely cause rather than shrugging.
                hardware.UnavailableReason =
                    "no controllable fan headers were found. The motherboard's SuperIO chip may " +
                    "not be supported, or the PawnIO driver may not be able to reach it. " +
                    $"See {PawnIoProbe.DownloadUrl}.";
                log.Warn($"fan control unavailable: {hardware.UnavailableReason}");
            }

            return hardware;
        }
        catch (Exception ex)
        {
            var reason = $"the hardware monitoring layer could not be opened: {ex.Message}";
            log.Warn($"fan control unavailable: {reason}");
            return new LhmFanHardware(log, computer: null, reason);
        }
    }

    void Discover()
    {
        _controls.Clear();
        _tachometers.Clear();

        var channels = new List<FanChannel>();
        foreach (var hardware in _computer!.Hardware) Collect(hardware, channels);

        _channels = channels;
    }

    void Collect(IHardware hardware, List<FanChannel> channels)
    {
        // A control and its tachometer are paired by index within the same chip. That is the
        // layout every SuperIO the library supports uses, but it is still a convention rather
        // than a guarantee, so a control with no matching tach is tolerated - it just loses the
        // RPM readout and cannot be verified at adoption.
        var tachometers = hardware.Sensors
            .Where(s => s.SensorType == SensorType.Fan)
            .GroupBy(s => s.Index)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var sensor in hardware.Sensors.Where(s => s.SensorType == SensorType.Control))
        {
            if (sensor.Control is null) continue;

            var identifier = sensor.Identifier.ToString();
            _controls[identifier] = sensor;

            string? tachIdentifier = null;
            if (tachometers.TryGetValue(sensor.Index, out var tach))
            {
                tachIdentifier = tach.Identifier.ToString();
                _tachometers[identifier] = tach;
            }

            channels.Add(new FanChannel
            {
                Identifier = identifier,
                Name = LabelFor(sensor.Index),
                ChipName = hardware.Name,
                Index = sensor.Index,
                MinSoftwarePercent = sensor.Control.MinSoftwareValue,
                MaxSoftwarePercent = sensor.Control.MaxSoftwareValue,
                RpmSensorIdentifier = tachIdentifier,
            });
        }

        foreach (var sub in hardware.SubHardware) Collect(sub, channels);
    }

    /// <summary>
    /// The header label as printed on the board, rather than the library's generic numbering.
    ///
    /// LibreHardwareMonitor names controls "Fan #1", "Fan #2" and so on, one-based and unrelated
    /// to anything written on the PCB — which is useless when the job at hand is matching a
    /// channel to a physical header. On ATX boards the SuperIO order follows the silkscreen:
    /// index 0 is the CPU header and the rest are the numbered chassis headers.
    ///
    /// That is a convention, not a guarantee, so this is only the *default* name. Adopting a
    /// header stores a <see cref="FanConfig.FriendlyName"/> that overrides it, and --identify-fan
    /// remains the way to be certain rather than trusting the label.
    /// </summary>
    static string LabelFor(int index) => index == 0 ? "CPU" : $"Chassis {index}";

    public void Refresh()
    {
        if (_computer is null) return;

        try
        {
            _computer.Accept(_visitor);
        }
        catch (Exception ex)
        {
            throw new FanHardwareException($"refreshing the SuperIO sensors failed: {ex.Message}", ex);
        }
    }

    public FanReading? Read(string controlIdentifier)
    {
        if (!_controls.TryGetValue(controlIdentifier, out var control)) return null;

        _tachometers.TryGetValue(controlIdentifier, out var tach);

        return new FanReading
        {
            Percent = control.Value,
            Rpm = tach?.Value is { } rpm ? (int)Math.Round(rpm) : null,
            Mode = control.Control?.ControlMode switch
            {
                ControlMode.Software => FanControlMode.Software,
                ControlMode.Default => FanControlMode.Default,
                _ => FanControlMode.Undefined,
            },
        };
    }

    public void SetPercent(string controlIdentifier, double percent)
    {
        var control = ControlFor(controlIdentifier);

        try
        {
            control.SetSoftware((float)Math.Clamp(percent, 0, 100));
        }
        catch (Exception ex)
        {
            throw new FanHardwareException(
                $"setting {controlIdentifier} to {percent:0.#}% failed: {ex.Message}", ex);
        }
    }

    public void ReleaseToDefault(string controlIdentifier)
    {
        var control = ControlFor(controlIdentifier);

        try
        {
            // Restores the PWM mode and value the library captured at Open(), which is what the
            // BIOS programmed at POST. This is the fail-safe primitive the whole design leans on.
            control.SetDefault();
        }
        catch (Exception ex)
        {
            throw new FanHardwareException(
                $"handing {controlIdentifier} back to the BIOS failed: {ex.Message}", ex);
        }
    }

    IControl ControlFor(string controlIdentifier)
    {
        if (!_controls.TryGetValue(controlIdentifier, out var sensor))
            throw new FanHardwareException($"no fan header '{controlIdentifier}' on this board");

        return sensor.Control
               ?? throw new FanHardwareException($"fan header '{controlIdentifier}' is not writable");
    }

    /// <summary>
    /// Closes and reopens everything after a run of failures — the case this exists for is a
    /// resume from sleep, where the chip may have been reinitialised underneath us.
    /// </summary>
    public bool TryReopen()
    {
        Close();

        try
        {
            var reopened = TryOpen(_log);
            if (!reopened.IsAvailable)
            {
                UnavailableReason = reopened.UnavailableReason;
                reopened.Close();
                return false;
            }

            _computer = reopened._computer;
            _channels = reopened._channels;

            _controls.Clear();
            foreach (var pair in reopened._controls) _controls[pair.Key] = pair.Value;

            _tachometers.Clear();
            foreach (var pair in reopened._tachometers) _tachometers[pair.Key] = pair.Value;

            UnavailableReason = null;
            return true;
        }
        catch (Exception ex)
        {
            UnavailableReason = $"reopening the hardware monitoring layer failed: {ex.Message}";
            return false;
        }
    }

    void Close()
    {
        var computer = _computer;
        _computer = null;
        _channels = [];
        _controls.Clear();
        _tachometers.Clear();

        try
        {
            computer?.Close();
        }
        catch (Exception ex)
        {
            _log.Warn($"closing the hardware monitoring layer failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Note the order: headers are released by <see cref="FanManager.ReleaseAll"/> before this
    /// runs. Closing first would drop the handles needed to hand them back.
    /// </summary>
    public void Dispose() => Close();
}
