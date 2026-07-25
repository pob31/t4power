namespace T4Power.Core.Fans;

/// <summary>Who is currently steering a channel.</summary>
public enum FanControlMode
{
    /// <summary>Not reported by the chip, or not read yet.</summary>
    Undefined,

    /// <summary>The SuperIO's own mode, i.e. the BIOS fan curve.</summary>
    Default,

    /// <summary>Driven by software — us, or something else that got there first.</summary>
    Software,
}

/// <summary>One writable PWM channel, as discovered on the board.</summary>
public sealed record FanChannel
{
    /// <summary>Canonical key, e.g. <c>/lpc/nct6701d/control/3</c>.</summary>
    public required string Identifier { get; init; }

    public required string Name { get; init; }

    /// <summary>The chip the channel lives on, e.g. "Nuvoton NCT6701D". Recorded so a header can
    /// still be found if the identifier string changes under us.</summary>
    public string? ChipName { get; init; }

    public int Index { get; init; } = -1;

    /// <summary>Bounds the chip itself will accept. Every commanded duty is clamped to these.</summary>
    public double MinSoftwarePercent { get; init; }
    public double MaxSoftwarePercent { get; init; } = 100;

    /// <summary>The tachometer paired with this channel, if one was found.</summary>
    public string? RpmSensorIdentifier { get; init; }

    public string Describe() => $"{Name} [{Identifier}]";
}

/// <summary>A point-in-time reading of one channel.</summary>
public sealed record FanReading
{
    public double? Percent { get; init; }
    public int? Rpm { get; init; }
    public FanControlMode Mode { get; init; }
}

/// <summary>Raised when the underlying hardware layer refuses an operation.</summary>
public sealed class FanHardwareException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// The seam between T4Power and whatever is actually talking to the SuperIO.
///
/// It exists so that <see cref="T4Power.Core"/> can own the decision-making without owning a
/// hardware library: the real implementation drags in LibreHardwareMonitor and a kernel driver,
/// and lives in the executable. Core keeps its zero-package property, and the tests get a fake.
/// </summary>
public interface IFanHardware : IDisposable
{
    bool IsAvailable { get; }

    /// <summary>Why fan control is off, in words a user can act on. Null when it is available.</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Where to go to fix it, when there is somewhere to go — a driver download page, typically.
    ///
    /// Carried as its own value rather than left inside <see cref="UnavailableReason"/> so the UI
    /// can offer a button instead of a URL the user has to notice and retype. Core never looks at
    /// it, which is what keeps this project ignorant of whatever the hardware layer depends on.
    /// </summary>
    string? UnavailableHelpUrl { get; }

    IReadOnlyList<FanChannel> Channels { get; }

    /// <summary>Pulls fresh values for every channel. Called once per service tick.</summary>
    void Refresh();

    FanReading? Read(string controlIdentifier);

    /// <summary>Takes the channel under software control at the given duty.</summary>
    void SetPercent(string controlIdentifier, double percent);

    /// <summary>Hands the channel back to the mode the BIOS programmed at POST.</summary>
    void ReleaseToDefault(string controlIdentifier);

    /// <summary>Re-opens the hardware after a run of failures. False if it is still broken.</summary>
    bool TryReopen();
}

/// <summary>
/// The stand-in used when no hardware layer was supplied — unit tests, and any build or machine
/// where fan control is not wired up. Reports itself unavailable and swallows every write, so
/// the rest of the service can treat fan control as strictly additive.
/// </summary>
public sealed class NullFanHardware : IFanHardware
{
    public bool IsAvailable => false;
    public string? UnavailableReason { get; } = "fan control is not available on this system";

    /// <summary>Nothing to link to: there is no hardware layer here to go and install.</summary>
    public string? UnavailableHelpUrl => null;

    public IReadOnlyList<FanChannel> Channels => [];

    public void Refresh() { }
    public FanReading? Read(string controlIdentifier) => null;
    public void SetPercent(string controlIdentifier, double percent) { }
    public void ReleaseToDefault(string controlIdentifier) { }
    public bool TryReopen() => false;
    public void Dispose() { }
}
