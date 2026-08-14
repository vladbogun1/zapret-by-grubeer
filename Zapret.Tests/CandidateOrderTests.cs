using Zapret.Core.AutoSelect;
using Zapret.Core.Model;

namespace Zapret.Tests;

public sealed class CandidateOrderTests
{
    private static StrategyDescriptor Usable(string id) => new()
    {
        Id = id,
        DisplayName = id,
        FilePath = id + ".bat",
        Arguments = ["--wf-tcp=443"],
    };

    private static StrategyDescriptor Broken(string id) => new()
    {
        Id = id,
        DisplayName = id,
        FilePath = id + ".bat",
        UnsupportedReason = "missing file",
    };

    private static readonly IReadOnlyList<StrategyDescriptor> Catalog =
    [
        Usable("general (ALT)"),
        Usable("general (ALT2)"),
        Broken("general (ALT3)"),
        Usable("general (ALT8)"),
        Usable("general"),
    ];

    [Fact]
    public void Without_memory_it_follows_the_catalogue_order()
    {
        var order = CandidateOrder.Build(Catalog, [], new SelectionMemory());

        // Upstream's own ordering, and the unusable strategy is never offered.
        Assert.Equal(["general (ALT)", "general (ALT2)", "general (ALT8)", "general"], order);
    }

    [Fact]
    public void What_worked_on_this_connection_is_tried_first()
    {
        var order = CandidateOrder.Build(Catalog, [], new SelectionMemory
        {
            LastWorkingOnNetwork = "general (ALT8)",
        });

        Assert.Equal("general (ALT8)", order[0]);
        Assert.Equal(4, order.Count);
        Assert.Single(order, id => id == "general (ALT8)");
    }

    [Fact]
    public void What_fixed_the_failing_service_elsewhere_comes_next()
    {
        var order = CandidateOrder.Build(Catalog, ["Discord", "YouTube"], new SelectionMemory
        {
            LastWorkingOnNetwork = "general",
            LastWorkingPerService = new Dictionary<string, string>
            {
                ["YouTube"] = "general (ALT2)",
                ["Discord"] = "general (ALT8)",
            },
        });

        // Network memory first, then per-service memory in the order the failures were reported.
        Assert.Equal(["general", "general (ALT8)", "general (ALT2)", "general (ALT)"], order);
    }

    [Fact]
    public void An_excluded_candidate_is_never_offered_again()
    {
        var memory = new SelectionMemory
        {
            LastWorkingOnNetwork = "general (ALT8)",
            Excluded = ["general (ALT8)", "general (ALT)"],
        };

        var order = CandidateOrder.Build(Catalog, [], memory);

        Assert.Equal(["general (ALT2)", "general"], order);
    }

    [Fact]
    public void Remembered_strategies_that_no_longer_exist_are_ignored()
    {
        var order = CandidateOrder.Build(Catalog, ["Discord"], new SelectionMemory
        {
            LastWorkingOnNetwork = "general (ALT20)",
            LastWorkingPerService = new Dictionary<string, string> { ["Discord"] = "general (ALT3)" },
        });

        // ALT20 is gone from this engine build and ALT3 is unusable in it; neither may be attempted.
        Assert.Equal(["general (ALT)", "general (ALT2)", "general (ALT8)", "general"], order);
    }

    /// <summary>
    /// Each candidate costs an engine restart and a probe, so an attempt is capped to stay short enough that a
    /// user will wait through it rather than close the window.
    /// </summary>
    [Fact]
    public void A_single_attempt_is_capped()
    {
        var many = Enumerable.Range(1, 21).Select(i => Usable($"general (ALT{i})")).ToList();

        var plan = CandidateOrder.Plan(many, [], new SelectionMemory());

        Assert.Equal(CandidateOrder.MaxAutomaticAttempts, plan.Count);
        Assert.Equal("general (ALT1)", plan[0]);
    }

    [Fact]
    public void A_small_catalogue_yields_a_short_plan_rather_than_padding()
    {
        var plan = CandidateOrder.Plan([Usable("general")], [], new SelectionMemory());

        Assert.Equal(["general"], plan);
    }

    /// <summary>A dead end is a product failure: the advice must always name something a person can do.</summary>
    [Theory]
    [InlineData(false, false, false, "advice.tryGameFilter")]
    [InlineData(true, true, false, "advice.widenIpSet")]
    [InlineData(true, false, true, "advice.sendReport")]
    [InlineData(true, false, false, "advice.tryFullSweep")]
    public void Advice_always_names_a_next_step(bool gameFilter, bool ipSet, bool exhausted, string expected) =>
        Assert.Equal(expected, CandidateOrder.AdviceFor(gameFilter, ipSet, exhausted));

    [Theory]
    [InlineData(50, "speed.fast")]
    [InlineData(200, "speed.normal")]
    [InlineData(800, "speed.slow")]
    public void Latency_is_explained_in_words_not_left_as_a_number(int ms, string expected) =>
        Assert.Equal(expected, new ServiceVerdict("Discord", true, ms, DateTimeOffset.UtcNow).SpeedKey);

    [Fact]
    public void An_unmeasured_service_has_nothing_to_say_about_speed() =>
        Assert.Null(new ServiceVerdict("Discord", false, null, DateTimeOffset.UtcNow).SpeedKey);
}
