using T4Power.Core.Model;

namespace T4Power.Tests;

/// <summary>
/// Config plumbing for fan headers: the v3 migration, and the edit helpers the IPC handlers use.
/// The migration matters more than it looks - a bad one either loses someone's profiles or starts
/// spinning a fan nobody asked it to touch.
/// </summary>
public class FanConfigTests
{
    static FanConfig Fan(string id = "/lpc/nct6701d/control/3") => new()
    {
        ControlIdentifier = id,
        SourceGpuUuid = "GPU-test-0001",
        Managed = true,
    };

    [Fact]
    public void Migrating_from_v2_adopts_no_fans_and_disturbs_nothing_else()
    {
        var v2 = new AppConfig
        {
            Version = 2,
            PollIntervalMs = 500,
            PipeAllowedSids = ["S-1-5-21-fake"],
            Gpus =
            [
                new GpuConfig
                {
                    Uuid = "GPU-test-0001",
                    Managed = true,
                    Profiles = [new Profile { Name = "MyCustom", PowerLimitW = 65 }],
                    Rules = [new Rule { Type = RuleType.ProcessName, ProfileName = "MyCustom", Match = ["audio.exe"] }],
                },
            ],
        };

        // No discovered GPUs at all: the v1->v2 step must not run and must not be able to wipe
        // the profiles of a config that was already past it.
        var migrated = v2.Migrate(new Dictionary<string, GpuInfo>());

        Assert.Equal(3, migrated.Version);
        Assert.Empty(migrated.Fans);

        var gpu = migrated.Gpus.Single();
        Assert.Equal("MyCustom", gpu.Profiles.Single().Name);
        Assert.Contains("audio.exe", gpu.Rules.Single().Match);
        Assert.Equal(500, migrated.PollIntervalMs);
        Assert.Equal(["S-1-5-21-fake"], migrated.PipeAllowedSids);
    }

    [Fact]
    public void An_up_to_date_config_is_returned_untouched()
    {
        var current = new AppConfig { Version = AppConfig.CurrentVersion, Fans = [Fan()] };
        Assert.Same(current, current.Migrate(new Dictionary<string, GpuInfo>()));
    }

    [Fact]
    public void WithFan_adds_a_newly_adopted_header_and_replaces_an_existing_one()
    {
        var config = new AppConfig().WithFan(Fan());
        Assert.Single(config.Fans);

        var renamed = config.WithFan(Fan() with { FriendlyName = "T4" });
        Assert.Single(renamed.Fans);
        Assert.Equal("T4", renamed.Fans[0].FriendlyName);

        var second = renamed.WithFan(Fan("/lpc/nct6701d/control/5"));
        Assert.Equal(2, second.Fans.Count);
    }

    [Fact]
    public void WithoutFan_forgets_only_the_named_header()
    {
        var config = new AppConfig()
            .WithFan(Fan())
            .WithFan(Fan("/lpc/nct6701d/control/5"))
            .WithoutFan("/lpc/nct6701d/control/3");

        Assert.Equal("/lpc/nct6701d/control/5", config.Fans.Single().ControlIdentifier);
    }

    [Fact]
    public void FindFan_ignores_identifier_casing()
    {
        var config = new AppConfig().WithFan(Fan());
        Assert.NotNull(config.FindFan("/LPC/NCT6701D/CONTROL/3"));
    }
}

public class FanCurveParsingTests
{
    [Fact]
    public void A_well_formed_list_parses_in_order()
    {
        Assert.True(FanCurve.TryParsePoints("49.5:27.26, 62.8:100", out var points, out var error));

        Assert.Null(error);
        Assert.Equal(2, points.Count);
        Assert.Equal(49.5, points[0].TemperatureC);
        Assert.Equal(27.26, points[0].Percent);
        Assert.Equal(100, points[1].Percent);
    }

    [Fact]
    public void Equals_works_as_well_as_colon()
    {
        Assert.True(FanCurve.TryParsePoints("50=30", out var points, out _));
        Assert.Equal(30, points.Single().Percent);
    }

    [Theory]
    [InlineData("49.5")]              // no percent
    [InlineData("49.5:")]
    [InlineData("banana:30")]
    [InlineData("50:30,broken")]      // one good pair, one bad
    [InlineData("50:130")]            // out of range
    [InlineData("50:-5")]
    [InlineData("")]
    public void A_malformed_list_fails_whole_rather_than_partially(string input)
    {
        // Half-applying a curve is worse than rejecting it: the fan would run on something the
        // user never wrote.
        Assert.False(FanCurve.TryParsePoints(input, out var points, out var error));

        Assert.Empty(points);
        Assert.NotNull(error);
    }
}

public class FanSelectorTests
{
    const string Id = "/lpc/nct6701d/control/3";

    static FanConfig Fan() => new()
    {
        ControlIdentifier = Id,
        SourceGpuUuid = "GPU-test-0001",
        ControlIndex = 3,
        FriendlyName = "T4 cooler",
    };

    [Theory]
    [InlineData(Id)]                        // the canonical form
    [InlineData("/LPC/NCT6701D/CONTROL/3")] // case insensitive
    [InlineData("control/3")]               // what --fans prints
    [InlineData("/control/3")]
    [InlineData("3")]                       // the index
    [InlineData("T4")]                      // a name substring
    [InlineData("cooler")]
    public void Recognised_selectors_match(string selector) =>
        Assert.True(FanSelector.Matches(Fan(), selector));

    [Theory]
    [InlineData("control/5")]
    [InlineData("5")]
    [InlineData("pump")]
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_does_not(string? selector) =>
        Assert.False(FanSelector.Matches(Fan(), selector));

    [Fact]
    public void A_bare_index_cannot_match_a_longer_one_by_suffix()
    {
        // "/lpc/nct6701d/control/13" ends with "3", and a naive suffix match would take it.
        var thirteen = Fan() with { ControlIdentifier = "/lpc/nct6701d/control/13", ControlIndex = 13 };

        Assert.False(FanSelector.Matches(thirteen, "control/3"));
        Assert.False(FanSelector.Matches(thirteen, "3"));
        Assert.True(FanSelector.Matches(thirteen, "control/13"));
    }

    [Fact]
    public void An_empty_selector_matches_nothing_rather_than_everything()
    {
        // The opposite of GpuSelector, deliberately: "set every unnamed fan to 100%" is not a
        // gesture that should be one missing argument away.
        Assert.False(FanSelector.Matches(Fan(), null));
        Assert.False(FanSelector.Matches(Fan(), "   "));
    }
}
