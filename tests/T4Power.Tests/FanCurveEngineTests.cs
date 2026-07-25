using T4Power.Core.Fans;
using T4Power.Core.Model;

namespace T4Power.Tests;

/// <summary>
/// The fan engine is pure in the same way the rule engine is - config and telemetry in, a
/// decision out, no motherboard anywhere - so all of this runs on any machine. The smoothing is
/// the part most likely to be subtly wrong, and a fan that steps on noise is audible, so that is
/// where most of these assertions are.
/// </summary>
public class FanCurveEngineTests
{
    // A controllable clock, since every smoothing assertion is about the passage of time.
    sealed class TestClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => Now += by;
        public void Advance(int seconds) => Advance(TimeSpan.FromSeconds(seconds));
    }

    const string Identifier = "/lpc/nct6701d/control/3";
    const string SourceUuid = "GPU-test-0001";

    /// <summary>A deliberately trivial curve: percent = (temp - 30) * 2, so any movement in the
    /// held temperature is obvious in the output.</summary>
    static FanCurve LinearCurve() => new()
    {
        Points =
        [
            new FanCurvePoint { TemperatureC = 30, Percent = 0 },
            new FanCurvePoint { TemperatureC = 80, Percent = 100 },
        ],
        MinTemperatureC = 20,
        MaxTemperatureC = 85,
        MinPercent = 0,
        MaxPercent = 100,
        Hysteresis = new FanHysteresis
        {
            ResponseTimeUpSeconds = 3,
            ResponseTimeDownSeconds = 30,
            HysteresisUpC = 2,
            HysteresisDownC = 1,
        },
    };

    static FanConfig Config(FanCurve? curve = null) => new()
    {
        ControlIdentifier = Identifier,
        SourceGpuUuid = SourceUuid,
        Managed = true,
        Curve = curve ?? LinearCurve(),
    };

    static GpuTelemetry Telemetry(TestClock clock, uint temp) => new()
    {
        Uuid = SourceUuid,
        TimestampUtc = clock.Now,
        TemperatureC = temp,
    };

    // ---- curve maths -----------------------------------------------------------------

    [Fact]
    public void Curve_is_flat_below_the_first_point_and_above_the_last()
    {
        var curve = FanCurve.DefaultForT4();

        Assert.Equal(27.26, curve.Evaluate(20), 2);
        Assert.Equal(27.26, curve.Evaluate(40), 2);
        Assert.Equal(27.26, curve.Evaluate(49.5), 2);
        Assert.Equal(100, curve.Evaluate(85), 2);
    }

    [Fact]
    public void Curve_interpolates_linearly_between_points()
    {
        var curve = FanCurve.DefaultForT4();

        // Halfway between 49.5 C and 62.8 C, so halfway between 27.26% and 100%.
        Assert.Equal(63.63, curve.Evaluate(56.15), 2);
        Assert.Equal(100, curve.Evaluate(62.8), 2);
        Assert.Equal(100, curve.Evaluate(75), 2);
    }

    [Fact]
    public void Curve_output_is_clamped_to_the_percent_bounds()
    {
        var curve = new FanCurve
        {
            Points = [new FanCurvePoint { TemperatureC = 40, Percent = 5 }],
            MinPercent = 20,
            MaxPercent = 90,
        };

        Assert.Equal(20, curve.Evaluate(40));

        var hot = curve with { Points = [new FanCurvePoint { TemperatureC = 40, Percent = 99 }] };
        Assert.Equal(90, hot.Evaluate(40));
    }

    [Fact]
    public void An_empty_curve_falls_back_to_the_floor_never_to_zero()
    {
        var curve = new FanCurve { Points = [], MinPercent = 20 };

        Assert.Equal(20, curve.Evaluate(70));
    }

    [Fact]
    public void Normalising_sorts_clamps_and_collapses_duplicate_temperatures()
    {
        var curve = new FanCurve
        {
            MinTemperatureC = 20,
            MaxTemperatureC = 85,
            Points =
            [
                new FanCurvePoint { TemperatureC = 70, Percent = 80 },
                new FanCurvePoint { TemperatureC = 5, Percent = 10 },      // below the axis
                new FanCurvePoint { TemperatureC = 120, Percent = 150 },   // beyond both bounds
                new FanCurvePoint { TemperatureC = 50, Percent = 40 },
            ],
        }.Normalised();

        Assert.Equal([20d, 50d, 70d, 85d], curve.Points.Select(p => p.TemperatureC));
        Assert.Equal(100, curve.Points[^1].Percent);   // percent clamped to 100
    }

    // ---- hysteresis band --------------------------------------------------------------

    [Fact]
    public void A_change_smaller_than_the_hysteresis_band_never_moves_the_fan()
    {
        var clock = new TestClock();
        var engine = new FanCurveEngine(() => clock.Now);
        var config = Config();

        var first = engine.Evaluate(config, Telemetry(clock, 50));
        Assert.Equal(40, first.Percent!.Value, 2);

        // +1 C is inside the 2 C band, so it does not matter how long it holds.
        for (var i = 0; i < 60; i++)
        {
            clock.Advance(1);
            var d = engine.Evaluate(config, Telemetry(clock, 51));
            Assert.Equal(40, d.Percent!.Value, 2);
        }
    }

    [Fact]
    public void Sawtooth_inside_the_band_produces_a_perfectly_constant_duty()
    {
        var clock = new TestClock();
        var engine = new FanCurveEngine(() => clock.Now);
        var config = Config();

        engine.Evaluate(config, Telemetry(clock, 50));

        // 51 is not a rise (needs +2), 49.5 rounds to 50 and is not a fall (needs -1).
        foreach (var temp in Enumerable.Range(0, 100).Select(i => i % 2 == 0 ? 51u : 50u))
        {
            clock.Advance(1);
            Assert.Equal(40, engine.Evaluate(config, Telemetry(clock, temp)).Percent!.Value, 2);
        }
    }

    // ---- response time ----------------------------------------------------------------

    [Fact]
    public void A_rise_takes_effect_only_after_the_up_response_time()
    {
        var clock = new TestClock();
        var engine = new FanCurveEngine(() => clock.Now);
        var config = Config();

        engine.Evaluate(config, Telemetry(clock, 50));

        // The response time is measured from the tick the rise is first seen, not from the tick
        // before it, so the clock below starts here.
        clock.Advance(1);
        Assert.Equal(40, engine.Evaluate(config, Telemetry(clock, 52)).Percent!.Value, 2);

        clock.Advance(2);   // 2 s of sustained rise, still short of the 3 s response time
        Assert.Equal(40, engine.Evaluate(config, Telemetry(clock, 52)).Percent!.Value, 2);

        clock.Advance(1);   // 3 s
        Assert.Equal(44, engine.Evaluate(config, Telemetry(clock, 52)).Percent!.Value, 2);
    }

    [Fact]
    public void A_fall_takes_effect_only_after_the_much_longer_down_response_time()
    {
        var clock = new TestClock();
        var engine = new FanCurveEngine(() => clock.Now);
        var config = Config();

        engine.Evaluate(config, Telemetry(clock, 50));

        clock.Advance(1);   // the fall is first seen here
        Assert.Equal(40, engine.Evaluate(config, Telemetry(clock, 49)).Percent!.Value, 2);

        clock.Advance(29);  // 29 s of sustained fall
        Assert.Equal(40, engine.Evaluate(config, Telemetry(clock, 49)).Percent!.Value, 2);

        clock.Advance(1);   // 30 s
        Assert.Equal(38, engine.Evaluate(config, Telemetry(clock, 49)).Percent!.Value, 2);
    }

    [Fact]
    public void A_candidate_that_reverses_direction_restarts_its_timer()
    {
        var clock = new TestClock();
        var engine = new FanCurveEngine(() => clock.Now);
        var config = Config();

        engine.Evaluate(config, Telemetry(clock, 50));

        clock.Advance(1);
        engine.Evaluate(config, Telemetry(clock, 52));   // rising candidate

        clock.Advance(1);
        engine.Evaluate(config, Telemetry(clock, 49));   // reverses: now a falling candidate

        // Had the rising timer survived, 3 s would have been enough to step up. It must not.
        clock.Advance(2);
        Assert.Equal(40, engine.Evaluate(config, Telemetry(clock, 49)).Percent!.Value, 2);

        // And the fall needs the full 30 s from the reversal, not from the original candidate.
        clock.Advance(27);
        Assert.Equal(40, engine.Evaluate(config, Telemetry(clock, 49)).Percent!.Value, 2);

        clock.Advance(1);
        Assert.Equal(38, engine.Evaluate(config, Telemetry(clock, 49)).Percent!.Value, 2);
    }

    [Fact]
    public void Returning_inside_the_band_cancels_a_pending_change()
    {
        var clock = new TestClock();
        var engine = new FanCurveEngine(() => clock.Now);
        var config = Config();

        engine.Evaluate(config, Telemetry(clock, 50));

        clock.Advance(1);
        engine.Evaluate(config, Telemetry(clock, 52));   // rising candidate

        clock.Advance(1);
        engine.Evaluate(config, Telemetry(clock, 51));   // back inside the band, candidate dropped

        // The earlier candidate must not resume where it left off.
        clock.Advance(2);
        Assert.Equal(40, engine.Evaluate(config, Telemetry(clock, 52)).Percent!.Value, 2);
    }

    [Fact]
    public void IgnoreAtLimits_makes_the_ends_of_the_curve_instantaneous()
    {
        var clock = new TestClock();
        var engine = new FanCurveEngine(() => clock.Now);
        var curve = LinearCurve();
        var config = Config(curve with
        {
            MaxTemperatureC = 60,
            Hysteresis = curve.Hysteresis with { IgnoreAtLimits = true },
        });

        engine.Evaluate(config, Telemetry(clock, 50));

        // At or beyond the top of the axis, the response time is bypassed entirely.
        clock.Advance(1);
        Assert.Equal(60, engine.Evaluate(config, Telemetry(clock, 62)).Percent!.Value, 2);
    }

    [Fact]
    public void Without_IgnoreAtLimits_the_ends_are_smoothed_like_everything_else()
    {
        var clock = new TestClock();
        var engine = new FanCurveEngine(() => clock.Now);
        var config = Config(LinearCurve() with { MaxTemperatureC = 60 });

        engine.Evaluate(config, Telemetry(clock, 50));

        clock.Advance(1);
        Assert.Equal(40, engine.Evaluate(config, Telemetry(clock, 62)).Percent!.Value, 2);
    }

    // ---- precedence --------------------------------------------------------------------

    [Fact]
    public void Panic_outranks_even_a_manual_override()
    {
        var clock = new TestClock();
        var engine = new FanCurveEngine(() => clock.Now);
        var config = Config() with
        {
            PanicTemperatureC = 80,
            Override = new FanOverride { Percent = 30 },
        };

        var d = engine.Evaluate(config, Telemetry(clock, 81));

        Assert.Equal(FanDecisionSource.Panic, d.Source);
        Assert.Equal(100, d.Percent);
    }

    [Fact]
    public void Panic_bypasses_the_response_time_entirely()
    {
        var clock = new TestClock();
        var engine = new FanCurveEngine(() => clock.Now);
        var config = Config() with { PanicTemperatureC = 80 };

        engine.Evaluate(config, Telemetry(clock, 50));

        // One tick later and 31 C hotter: no waiting, no smoothing.
        clock.Advance(1);
        var d = engine.Evaluate(config, Telemetry(clock, 81));

        Assert.Equal(FanDecisionSource.Panic, d.Source);
        Assert.Equal(100, d.Percent);
    }

    [Fact]
    public void An_override_beats_the_curve_until_it_expires()
    {
        var clock = new TestClock();
        var engine = new FanCurveEngine(() => clock.Now);
        var config = Config() with
        {
            Override = new FanOverride { Percent = 65, ExpiresUtc = clock.Now.AddSeconds(30) },
        };

        var held = engine.Evaluate(config, Telemetry(clock, 50));
        Assert.Equal(FanDecisionSource.Override, held.Source);
        Assert.Equal(65, held.Percent);

        clock.Advance(31);
        var lapsed = engine.Evaluate(config, Telemetry(clock, 50));
        Assert.Equal(FanDecisionSource.Curve, lapsed.Source);
    }

    [Fact]
    public void An_unmanaged_header_yields_no_duty_at_all()
    {
        var clock = new TestClock();
        var engine = new FanCurveEngine(() => clock.Now);

        var d = engine.Evaluate(Config() with { Managed = false }, Telemetry(clock, 70));

        Assert.Equal(FanDecisionSource.Unmanaged, d.Source);
        Assert.Null(d.Percent);
        Assert.False(d.ReleaseToDefault);
    }

    // ---- fail-safe ---------------------------------------------------------------------

    [Fact]
    public void No_telemetry_hands_the_header_back_to_the_bios()
    {
        var engine = new FanCurveEngine();

        var d = engine.Evaluate(Config(), telemetry: null);

        Assert.Equal(FanDecisionSource.FailSafe, d.Source);
        Assert.True(d.ReleaseToDefault);
        Assert.Null(d.Percent);
    }

    [Fact]
    public void A_gpu_reporting_no_temperature_trips_the_failsafe()
    {
        var clock = new TestClock();
        var engine = new FanCurveEngine(() => clock.Now);

        var d = engine.Evaluate(Config(), new GpuTelemetry
        {
            Uuid = SourceUuid,
            TimestampUtc = clock.Now,
            TemperatureC = null,
        });

        Assert.Equal(FanDecisionSource.FailSafe, d.Source);
    }

    [Fact]
    public void Telemetry_older_than_the_timeout_trips_the_failsafe()
    {
        var clock = new TestClock();
        var engine = new FanCurveEngine(() => clock.Now);
        var config = Config() with { SensorTimeoutSeconds = 10 };

        var stale = Telemetry(clock, 50);
        clock.Advance(11);

        var d = engine.Evaluate(config, stale);

        Assert.Equal(FanDecisionSource.FailSafe, d.Source);
        Assert.True(d.ReleaseToDefault);
    }

    [Fact]
    public void The_failsafe_mode_decides_between_bios_and_a_fixed_duty()
    {
        var engine = new FanCurveEngine();

        var full = engine.Evaluate(Config() with { FailSafe = FanFailSafe.FullSpeed }, null);
        Assert.Equal(100, full.Percent);
        Assert.False(full.ReleaseToDefault);

        var fixedDuty = engine.Evaluate(
            Config() with { FailSafe = FanFailSafe.FixedPercent, FailSafePercent = 70 }, null);
        Assert.Equal(70, fixedDuty.Percent);
    }

    [Fact]
    public void Recovering_from_the_failsafe_starts_the_smoothing_afresh()
    {
        var clock = new TestClock();
        var engine = new FanCurveEngine(() => clock.Now);
        var config = Config();

        engine.Evaluate(config, Telemetry(clock, 50));
        engine.Evaluate(config, telemetry: null);          // state discarded

        // The first reading after a gap is adopted immediately rather than being smoothed from a
        // held value that predates the outage.
        clock.Advance(1);
        Assert.Equal(60, engine.Evaluate(config, Telemetry(clock, 60)).Percent!.Value, 2);
    }

    // ---- state hygiene -----------------------------------------------------------------

    [Fact]
    public void Reset_drops_the_smoothing_state_for_one_header()
    {
        var clock = new TestClock();
        var engine = new FanCurveEngine(() => clock.Now);
        var config = Config();

        engine.Evaluate(config, Telemetry(clock, 50));
        engine.Reset(Identifier);

        clock.Advance(1);
        Assert.Equal(60, engine.Evaluate(config, Telemetry(clock, 60)).Percent!.Value, 2);
    }

    [Fact]
    public void Two_headers_keep_independent_smoothing_state()
    {
        var clock = new TestClock();
        var engine = new FanCurveEngine(() => clock.Now);
        var first = Config();
        var second = Config() with { ControlIdentifier = "/lpc/nct6701d/control/5" };

        engine.Evaluate(first, Telemetry(clock, 50));
        engine.Evaluate(second, Telemetry(clock, 70));

        clock.Advance(1);
        Assert.Equal(40, engine.Evaluate(first, Telemetry(clock, 50)).Percent!.Value, 2);
        Assert.Equal(80, engine.Evaluate(second, Telemetry(clock, 70)).Percent!.Value, 2);
    }

    [Fact]
    public void Concurrent_evaluation_does_not_corrupt_the_smoothing_state()
    {
        // The service reaches this from both the poll timer and IPC handlers. A Dictionary written
        // concurrently does not fail cleanly - it can spin forever, which for a fan means a duty
        // that never updates again.
        var engine = new FanCurveEngine();
        var config = Config();

        Parallel.For(0, 2000, i =>
        {
            var telemetry = new GpuTelemetry
            {
                Uuid = SourceUuid,
                TimestampUtc = DateTimeOffset.UtcNow,
                TemperatureC = (uint)(40 + i % 30),
            };
            engine.Evaluate(config, telemetry);
        });

        Assert.NotNull(engine.Evaluate(config, new GpuTelemetry
        {
            Uuid = SourceUuid,
            TimestampUtc = DateTimeOffset.UtcNow,
            TemperatureC = 55,
        }).Percent);
    }
}
