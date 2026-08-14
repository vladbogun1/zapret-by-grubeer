using Zapret.Core.AutoSelect;
using Zapret.Core.Engine;
using Zapret.Core.Model;

namespace Zapret.Tests;

public sealed class AutoSelectServiceTests
{
    private const string Network = "net-1";

    // ---- fakes -------------------------------------------------------------------------------

    private sealed class FakeRuntime(IReadOnlyList<string> strategyIds, GameFilterMode game = GameFilterMode.Off, IpSetMode ipSet = IpSetMode.Any)
        : IFlowsealAdapterProvider
    {
        public EngineRuntimeInfo? Current { get; } = new()
        {
            Directory = @"C:\engine",
            Version = new EngineVersion("1.10.1", EngineVersionSource.ServiceVersionFile),
            Capabilities = UpstreamCapabilities.None,
            Report = new CompatibilityReport(CompatibilityOutcome.Compatible, []),
            GameFilter = new GameFilterState(game),
            IpSet = ipSet,
            Strategies = strategyIds.Select(id => new StrategyDescriptor
            {
                Id = id,
                DisplayName = id,
                FilePath = id + ".bat",
                Arguments = ["--wf-tcp=443"],
            }).ToList(),
        };
    }

    private sealed class FakeController : IEngineController
    {
        public EngineState State { get; private set; } = EngineState.Stopped;
        public List<string> Started { get; } = new();
        public int StopCount { get; private set; }
        public HashSet<string> RefuseToStart { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<bool> StartAsync(EngineRuntimeInfo runtime, StrategyDescriptor strategy, CancellationToken cancellationToken = default)
        {
            if (RefuseToStart.Contains(strategy.Id)) return Task.FromResult(false);

            Started.Add(strategy.Id);
            State = new EngineState(EngineStatus.Running, strategy.Id, runtime.Version.Raw, DateTimeOffset.UtcNow);
            return Task.FromResult(true);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            State = EngineState.Stopped;
            return Task.CompletedTask;
        }

        public Task<bool> IsHealthyAsync(TimeSpan settle, CancellationToken cancellationToken = default) =>
            Task.FromResult(State.Status == EngineStatus.Running);
    }

    /// <summary>Reachability depends on which strategy is running, which is the whole point of selection.</summary>
    private sealed class FakeProbe(FakeController controller, IReadOnlyDictionary<string, IReadOnlyList<string>> fixedBy)
        : IServiceProbe
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<ServiceVerdict>> ProbeAsync(IReadOnlyList<string> serviceIds, CancellationToken cancellationToken)
        {
            Calls++;

            var running = controller.State.Status == EngineStatus.Running ? controller.State.StrategyId : null;
            var working = running is not null && fixedBy.TryGetValue(running, out var list) ? list : Array.Empty<string>();

            return Task.FromResult<IReadOnlyList<ServiceVerdict>>(serviceIds
                .Select(id => new ServiceVerdict(id, working.Contains(id), working.Contains(id) ? 40 : null, DateTimeOffset.UtcNow))
                .ToList());
        }
    }

    private sealed class MemoryStore : ISelectionMemoryStore
    {
        public SelectionMemory Memory { get; set; } = new();
        public List<(string Network, string Strategy)> Remembered { get; } = new();

        public SelectionMemory Read(string networkId) => Memory;

        public void RememberWorking(string networkId, string strategyId, IReadOnlyList<string> fixedServices) =>
            Remembered.Add((networkId, strategyId));
    }

    private static AutoSelectService Create(
        FakeRuntime runtime,
        FakeController controller,
        IServiceProbe probe,
        ISelectionMemoryStore memory) =>
        // No settle delay in tests: the suite must stay fast enough that people keep running it.
        new(runtime, controller, probe, memory, TimeSpan.Zero);

    private static readonly string[] Watched = ["Discord", "YouTube"];

    // ---- tests -------------------------------------------------------------------------------

    /// <summary>
    /// The question the 1.x flow never asked. On a connection where nothing is blocked, the answer arrives in
    /// seconds and no strategy is applied at all.
    /// </summary>
    [Fact]
    public async Task When_nothing_is_blocked_no_strategy_is_applied()
    {
        var controller = new FakeController();

        // Everything is reachable with the engine stopped: the fake reports success for the "no strategy" key.
        var probe = new AlwaysReachableProbe();
        var outcome = await Create(new FakeRuntime(["general", "general (ALT)"]), controller, probe, new MemoryStore())
            .RunAsync(Watched, Network);

        Assert.True(outcome.Success);
        Assert.False(outcome.BypassNeeded);
        Assert.Null(outcome.StrategyId);
        Assert.Empty(controller.Started);
        Assert.Equal(0, outcome.Attempts);
        Assert.Contains(outcome.Steps, s => s.MessageKey == "step.noBypassNeeded");
    }

    private sealed class AlwaysReachableProbe : IServiceProbe
    {
        public Task<IReadOnlyList<ServiceVerdict>> ProbeAsync(IReadOnlyList<string> serviceIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ServiceVerdict>>(
                serviceIds.Select(id => new ServiceVerdict(id, true, 30, DateTimeOffset.UtcNow)).ToList());
    }

    [Fact]
    public async Task It_stops_at_the_first_candidate_that_fixes_everything()
    {
        var controller = new FakeController();
        var probe = new FakeProbe(controller, new Dictionary<string, IReadOnlyList<string>>
        {
            ["general (ALT2)"] = Watched,
        });

        var memory = new MemoryStore();
        var outcome = await Create(new FakeRuntime(["general", "general (ALT)", "general (ALT2)", "general (ALT8)"]),
            controller, probe, memory).RunAsync(Watched, Network);

        Assert.True(outcome.Success);
        Assert.Equal("general (ALT2)", outcome.StrategyId);
        Assert.True(outcome.BypassNeeded);

        // Tried in catalogue order and stopped immediately: ALT8 was never started.
        Assert.Equal(["general", "general (ALT)", "general (ALT2)"], controller.Started);
        Assert.Equal(3, outcome.Attempts);
        Assert.Equal(("net-1", "general (ALT2)"), memory.Remembered.Single());
    }

    [Fact]
    public async Task What_worked_before_is_tried_first_so_the_common_case_is_one_attempt()
    {
        var controller = new FakeController();
        var probe = new FakeProbe(controller, new Dictionary<string, IReadOnlyList<string>>
        {
            ["general (ALT8)"] = Watched,
        });

        var memory = new MemoryStore { Memory = new SelectionMemory { LastWorkingOnNetwork = "general (ALT8)" } };

        var outcome = await Create(new FakeRuntime(["general", "general (ALT)", "general (ALT8)"]), controller, probe, memory)
            .RunAsync(Watched, Network);

        Assert.True(outcome.Success);
        Assert.Equal(1, outcome.Attempts);
        Assert.Equal(["general (ALT8)"], controller.Started);
    }

    /// <summary>A partial fix is not a fix: the user named both services, so both must work.</summary>
    [Fact]
    public async Task A_candidate_that_fixes_only_some_services_is_rejected()
    {
        var controller = new FakeController();
        var probe = new FakeProbe(controller, new Dictionary<string, IReadOnlyList<string>>
        {
            ["general"] = ["Discord"],
            ["general (ALT)"] = Watched,
        });

        var outcome = await Create(new FakeRuntime(["general", "general (ALT)"]), controller, probe, new MemoryStore())
            .RunAsync(Watched, Network);

        Assert.True(outcome.Success);
        Assert.Equal("general (ALT)", outcome.StrategyId);
        Assert.Equal(["general", "general (ALT)"], controller.Started);
    }

    [Fact]
    public async Task A_candidate_that_refuses_to_start_is_skipped_not_fatal()
    {
        var controller = new FakeController();
        controller.RefuseToStart.Add("general");

        var probe = new FakeProbe(controller, new Dictionary<string, IReadOnlyList<string>>
        {
            ["general (ALT)"] = Watched,
        });

        var outcome = await Create(new FakeRuntime(["general", "general (ALT)"]), controller, probe, new MemoryStore())
            .RunAsync(Watched, Network);

        Assert.True(outcome.Success);
        Assert.Equal("general (ALT)", outcome.StrategyId);
        Assert.DoesNotContain("general", controller.Started);
    }

    /// <summary>
    /// Exhausting the candidates must not leave a useless engine running, and must not end in a dead end: the
    /// advice names something a person can actually do.
    /// </summary>
    [Fact]
    public async Task When_nothing_works_it_stops_the_engine_and_advises()
    {
        var controller = new FakeController();
        var probe = new FakeProbe(controller, new Dictionary<string, IReadOnlyList<string>>());

        var outcome = await Create(new FakeRuntime(["general", "general (ALT)"]), controller, probe, new MemoryStore())
            .RunAsync(Watched, Network);

        Assert.False(outcome.Success);
        Assert.True(outcome.BypassNeeded);
        Assert.Equal("advice.tryGameFilter", outcome.AdviceKey);
        Assert.Equal(EngineStatus.Stopped, controller.State.Status);
        Assert.Contains(outcome.Steps, s => s.MessageKey == "step.exhausted");
    }

    [Fact]
    public async Task The_advice_moves_on_once_the_game_filter_is_already_enabled()
    {
        var controller = new FakeController();
        var probe = new FakeProbe(controller, new Dictionary<string, IReadOnlyList<string>>());

        var outcome = await Create(
                new FakeRuntime(["general", "general (ALT)"], GameFilterMode.All, IpSetMode.Loaded),
                controller, probe, new MemoryStore())
            .RunAsync(Watched, Network);

        Assert.Equal("advice.widenIpSet", outcome.AdviceKey);
    }

    [Fact]
    public async Task Watching_nothing_is_a_valid_answer_and_does_no_work()
    {
        var controller = new FakeController();
        var probe = new FakeProbe(controller, new Dictionary<string, IReadOnlyList<string>>());

        var outcome = await Create(new FakeRuntime(["general"]), controller, probe, new MemoryStore())
            .RunAsync([], Network);

        Assert.True(outcome.Success);
        Assert.False(outcome.BypassNeeded);
        Assert.Equal(0, probe.Calls);
        Assert.Equal(0, controller.StopCount);
    }

    [Fact]
    public async Task Without_an_engine_it_says_so_instead_of_probing()
    {
        var controller = new FakeController();
        var probe = new FakeProbe(controller, new Dictionary<string, IReadOnlyList<string>>());

        var outcome = await new AutoSelectService(new NoRuntime(), controller, probe, new MemoryStore(), TimeSpan.Zero)
            .RunAsync(Watched, Network);

        Assert.False(outcome.Success);
        Assert.Equal("advice.installEngine", outcome.AdviceKey);
        Assert.Equal(0, probe.Calls);
    }

    private sealed class NoRuntime : IFlowsealAdapterProvider
    {
        public EngineRuntimeInfo? Current => null;
    }

    [Fact]
    public async Task Progress_is_reported_as_real_steps()
    {
        var controller = new FakeController();
        var probe = new FakeProbe(controller, new Dictionary<string, IReadOnlyList<string>>
        {
            ["general"] = Watched,
        });

        var reported = new List<ProgressStep>();
        var progress = new Progress<ProgressStep>(reported.Add);

        await Create(new FakeRuntime(["general"]), controller, probe, new MemoryStore())
            .RunAsync(Watched, Network, progress);

        // Steps are pushed as they happen, and the first one is announced before it completes.
        await Task.Delay(50);
        Assert.Contains(reported, s => s.MessageKey == "step.checkingWithoutBypass" && !s.Done);
        Assert.Contains(reported, s => s.MessageKey == "step.fixed");
    }
}
