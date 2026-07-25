using System.Text.Json.Serialization;

namespace T4Power.Core.Model;

/// <summary>What to do with a header when T4Power cannot steer it responsibly any more.</summary>
public enum FanFailSafe
{
    /// <summary>
    /// Hand the channel back to the SuperIO's own mode, i.e. whatever the BIOS programmed at POST.
    /// The default, and the only option that also covers a crash: the BIOS setting survives us.
    /// It does mean the BIOS fan curve for this header has to be safe in its own right — see the
    /// "Motherboard fan control" section of the README.
    /// </summary>
    BiosDefault,

    /// <summary>Hold the header at full speed. Loud, but independent of the BIOS configuration.</summary>
    FullSpeed,

    /// <summary>Hold the header at <see cref="FanConfig.FailSafePercent"/>.</summary>
    FixedPercent,
}

/// <summary>One handle on the curve: at this temperature, run the fan at this duty.</summary>
public sealed record FanCurvePoint
{
    public required double TemperatureC { get; init; }
    public required double Percent { get; init; }
}

/// <summary>
/// How aggressively the curve is allowed to chase the temperature.
///
/// Two independent mechanisms, and they compose: the hysteresis band decides whether a reading
/// counts as a change at all, and the response time decides how long that change has to persist
/// before it is acted on. Defaults are asymmetric on purpose — ramp up quickly because heat is
/// already happening, ramp down slowly because a fan that chases every dip audibly hunts.
/// </summary>
public sealed record FanHysteresis
{
    public double ResponseTimeUpSeconds { get; init; } = 3;
    public double ResponseTimeDownSeconds { get; init; } = 30;

    /// <summary>A rise smaller than this is treated as noise and moves nothing.</summary>
    public double HysteresisUpC { get; init; } = 2;

    /// <summary>A fall smaller than this is treated as noise and moves nothing.</summary>
    public double HysteresisDownC { get; init; } = 1;

    /// <summary>
    /// When true, readings at or beyond the curve's temperature bounds bypass both mechanisms and
    /// take effect at once. Useful if you want the top of the curve to be instantaneous.
    /// </summary>
    public bool IgnoreAtLimits { get; init; }
}

/// <summary>
/// A fan curve: duty as a piecewise-linear function of temperature, plus the smoothing rules.
/// Pure data and pure maths — <see cref="Evaluate"/> has no state, which is what lets the whole
/// thing be tested without a motherboard.
/// </summary>
public sealed record FanCurve
{
    public IReadOnlyList<FanCurvePoint> Points { get; init; } = [];

    /// <summary>Ends of the temperature axis. Editor bounds, and the clamp applied before lookup.</summary>
    public double MinTemperatureC { get; init; } = 20;
    public double MaxTemperatureC { get; init; } = 85;

    /// <summary>
    /// Floor on the commanded duty. Defaults to 20 rather than 0 deliberately: a blower commanded
    /// below its stall threshold reads 0 RPM, which is strictly worse than a slow fan. Set it to 0
    /// if you genuinely want the header to be able to stop.
    /// </summary>
    public double MinPercent { get; init; } = 20;

    public double MaxPercent { get; init; } = 100;

    public FanHysteresis Hysteresis { get; init; } = new();

    /// <summary>
    /// Duty at a given temperature. Piecewise linear between points, flat beyond both ends — the
    /// same shape the curve draws in the editor — then clamped to
    /// <see cref="MinPercent"/>..<see cref="MaxPercent"/>.
    /// </summary>
    public double Evaluate(double temperatureC)
    {
        // No curve is not a reason to stop the fan. Fall back to the floor, never to zero.
        if (Points.Count == 0) return MinPercent;

        var points = Points;
        var raw = points[0].Percent;

        if (temperatureC <= points[0].TemperatureC)
        {
            raw = points[0].Percent;
        }
        else if (temperatureC >= points[^1].TemperatureC)
        {
            raw = points[^1].Percent;
        }
        else
        {
            for (var i = 0; i < points.Count - 1; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                if (temperatureC < a.TemperatureC || temperatureC > b.TemperatureC) continue;

                var span = b.TemperatureC - a.TemperatureC;
                // Two points at the same temperature: a vertical step. Take the upper one.
                raw = span <= 0
                    ? b.Percent
                    : a.Percent + (b.Percent - a.Percent) * ((temperatureC - a.TemperatureC) / span);
                break;
            }
        }

        return Math.Clamp(raw, MinPercent, MaxPercent);
    }

    /// <summary>
    /// Puts a user-supplied curve into the shape <see cref="Evaluate"/> expects: sorted by
    /// temperature, clamped to the axis bounds, and with duplicate temperatures collapsed.
    ///
    /// Points arrive from the pipe and from the curve editor, so neither ordering nor range can be
    /// assumed. Everything else is preserved — a redundant-looking point is still the user's
    /// point, and silently deleting handles they dragged would be worse than carrying them.
    /// </summary>
    public FanCurve Normalised()
    {
        if (Points.Count == 0) return this;

        var cleaned = new List<FanCurvePoint>(Points.Count);

        foreach (var point in Points.OrderBy(p => p.TemperatureC))
        {
            var temperature = Math.Clamp(point.TemperatureC, MinTemperatureC, MaxTemperatureC);
            var percent = Math.Clamp(point.Percent, 0, 100);

            // Clamping can push two points onto the same temperature; the later one wins so the
            // curve stays a function.
            if (cleaned.Count > 0 && Math.Abs(cleaned[^1].TemperatureC - temperature) < 0.0001)
                cleaned.RemoveAt(cleaned.Count - 1);

            cleaned.Add(new FanCurvePoint { TemperatureC = temperature, Percent = percent });
        }

        return this with { Points = cleaned };
    }

    /// <summary>
    /// Parses <c>"49.5:27.26, 62.8:100"</c> into curve points.
    ///
    /// Lives here rather than in the CLI parser so it can be unit-tested without dragging the
    /// executable — and its hardware library — into the test project, and so the UI can accept
    /// the same text.
    ///
    /// All-or-nothing: a malformed pair fails the whole list rather than being skipped. Silently
    /// dropping the pair someone fat-fingered would leave a fan running on a curve they did not
    /// write, which is a bad way to find out about a typo.
    /// </summary>
    public static bool TryParsePoints(string value, out IReadOnlyList<FanCurvePoint> points, out string? error)
    {
        points = [];

        var parsed = new List<FanCurvePoint>();
        var pairs = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var pair in pairs)
        {
            var halves = pair.Split([':', '='], 2, StringSplitOptions.TrimEntries);

            if (halves.Length != 2
                || !double.TryParse(halves[0], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var temperature)
                || !double.TryParse(halves[1], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var percent))
            {
                error = $"expected 'temp:percent' pairs like '49.5:27,62.8:100'; got '{pair}'";
                return false;
            }

            if (percent is < 0 or > 100)
            {
                error = $"percent must be 0-100; got '{pair}'";
                return false;
            }

            parsed.Add(new FanCurvePoint { TemperatureC = temperature, Percent = percent });
        }

        if (parsed.Count == 0)
        {
            error = "at least one 'temp:percent' pair is needed";
            return false;
        }

        points = parsed;
        error = null;
        return true;
    }

    /// <summary>
    /// The curve this feature was built to reproduce: the "T4 graph" that drove the N3rdware
    /// cooler under FanControl, carried over so the cutover changes nothing audible.
    /// </summary>
    public static FanCurve DefaultForT4() => new()
    {
        Points =
        [
            new FanCurvePoint { TemperatureC = 49.5, Percent = 27.26 },
            new FanCurvePoint { TemperatureC = 62.8, Percent = 100 },
            new FanCurvePoint { TemperatureC = 77.8, Percent = 100 },
        ],
        MinTemperatureC = 20,
        MaxTemperatureC = 85,
        MinPercent = 20,
        MaxPercent = 100,
        Hysteresis = new FanHysteresis
        {
            ResponseTimeUpSeconds = 3,
            ResponseTimeDownSeconds = 30,
            HysteresisUpC = 2,
            HysteresisDownC = 1,
            IgnoreAtLimits = false,
        },
    };
}

/// <summary>
/// A manual duty held on a header, from the UI slider or <c>--fan-set</c>. Beats the curve but
/// not the panic temperature. Expiry is evaluated service-side, like <see cref="Override"/>, so a
/// TTL still fires if the client that set it has gone away.
/// </summary>
public sealed record FanOverride
{
    public required double Percent { get; init; }
    public DateTimeOffset? ExpiresUtc { get; init; }

    public bool IsExpired(DateTimeOffset now) => ExpiresUtc is not null && now >= ExpiresUtc;
}

/// <summary>One motherboard fan header that T4Power has been told to drive.</summary>
public sealed record FanConfig
{
    /// <summary>
    /// The library's identifier for the control channel, e.g. <c>/lpc/nct6701d/control/3</c>.
    /// Primary key. See <see cref="ChipName"/> for what happens when it moves.
    /// </summary>
    public required string ControlIdentifier { get; init; }

    /// <summary>
    /// Chip and index, recorded alongside the identifier purely so the header can be re-found if
    /// the identifier string changes — a BIOS update or a library rename is enough to do that, and
    /// silently managing nothing is much better than silently managing the wrong fan.
    /// </summary>
    public string? ChipName { get; init; }
    public int ControlIndex { get; init; } = -1;

    /// <summary>The tachometer paired with this control, if one was found. Read-only; used for
    /// the RPM display and for verifying at adoption that the header actually responds.</summary>
    public string? RpmSensorIdentifier { get; init; }

    public string? FriendlyName { get; init; }

    /// <summary>
    /// When false, T4Power reports on this header but never writes to it.
    ///
    /// Defaults to false, unlike <see cref="GpuConfig.Managed"/>. There is no equivalent of
    /// "it is obviously a Tesla T4" for a fan header — nothing about a PWM channel says which
    /// device it cools, so adoption is always an explicit act.
    /// </summary>
    public bool Managed { get; init; }

    /// <summary>UUID of the GPU whose temperature drives this header.</summary>
    public required string SourceGpuUuid { get; init; }

    public FanCurve Curve { get; init; } = new();
    public FanOverride? Override { get; init; }

    /// <summary>
    /// Full speed at or above this temperature, bypassing the curve, the smoothing and any manual
    /// override. Sits below <see cref="GpuConfig.ThermalGuardC"/> (85 on a T4) on purpose: try
    /// moving more air before giving up and clamping the card's clocks.
    /// </summary>
    public double PanicTemperatureC { get; init; } = 80;

    /// <summary>
    /// How stale the source GPU's telemetry may get before the header is considered unsteerable
    /// and the fail-safe takes over.
    /// </summary>
    public int SensorTimeoutSeconds { get; init; } = 10;

    public FanFailSafe FailSafe { get; init; } = FanFailSafe.BiosDefault;
    public double FailSafePercent { get; init; } = 100;

    /// <summary>
    /// True once the header has been commanded and seen to change RPM. Adoption sets this; a
    /// header that never proved it responds is worth flagging in the UI, because the failure mode
    /// is driving the wrong fan.
    /// </summary>
    public bool Verified { get; init; }

    /// <summary>Name for logs and messages. Computed, so it is kept out of the serialised config —
    /// a derived value written to disk is just a second copy to go stale.</summary>
    [JsonIgnore]
    public string DisplayName => FriendlyName is { Length: > 0 } name ? name : ControlIdentifier;

    public static FanConfig CreateDefault(string controlIdentifier, string sourceGpuUuid) => new()
    {
        ControlIdentifier = controlIdentifier,
        SourceGpuUuid = sourceGpuUuid,
        Managed = true,
        Curve = FanCurve.DefaultForT4(),
    };
}
