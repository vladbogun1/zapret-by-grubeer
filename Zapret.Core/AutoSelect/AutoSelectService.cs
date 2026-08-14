using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zapret.Core.Engine;
using Zapret.Core.Model;

namespace Zapret.Core.AutoSelect;

/// <summary>Probes the services a user named, and nothing else.</summary>
public interface IServiceProbe
{
    Task<IReadOnlyList<ServiceVerdict>> ProbeAsync(IReadOnlyList<string> serviceIds, CancellationToken cancellationToken);
}

/// <summary>What the product learned, so the next selection is faster than this one.</summary>
public interface ISelectionMemoryStore
{
    SelectionMemory Read(string networkId);

    void RememberWorking(string networkId, string strategyId, IReadOnlyList<string> fixedServices);
}

public sealed record AutoSelectOutcome
{
    public required bool Success { get; init; }

    /// <summary>The strategy that fixed it, or null when no bypass was needed at all.</summary>
    public string? StrategyId { get; init; }

    public IReadOnlyList<ServiceVerdict> Verdicts { get; init; } = Array.Empty<ServiceVerdict>();

    /// <summary>False when everything the user named already worked with the bypass off.</summary>
    public bool BypassNeeded { get; init; }

    /// <summary>Set only on failure, and always names something a person can do.</summary>
    public string? AdviceKey { get; init; }

    public IReadOnlyList<ProgressStep> Steps { get; init; } = Array.Empty<ProgressStep>();

    public int Attempts { get; init; }
}

/// <summary>
/// Targeted, incremental strategy selection — the primary path of the 2.0 product
/// (docs/nextgen-ux.md §4).
/// <para>
/// The 1.x product measured all 21 strategies against 17 targets for about five minutes with the bypass down,
/// then asked the user to choose. Nobody waits five minutes to open Discord. This measures only what the user
/// named, tries only the likely candidates, and stops at the first one that works.
/// </para>
/// <para>
/// It also asks a question the old flow never did: <b>is a bypass needed at all?</b> On a connection where
/// nothing is blocked, the honest answer arrives in seconds and no strategy is applied.
/// </para>
/// </summary>
public sealed class AutoSelectService(
    IFlowsealAdapterProvider runtime,
    IEngineController controller,
    IServiceProbe probe,
    ISelectionMemoryStore memory,
    TimeSpan? settleDelay = null,
    ILogger<AutoSelectService>? logger = null)
{
    /// <summary>
    /// How long a freshly started engine is given before its effect is measured. Long enough for WinDivert to
    /// be capturing, short enough that six candidates stay within a minute of waiting. Injectable so tests do
    /// not pay for it — a suite that sleeps is a suite people stop running.
    /// </summary>
    public static readonly TimeSpan DefaultSettleDelay = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _settle = settleDelay ?? DefaultSettleDelay;
    private readonly ILogger _logger = logger ?? NullLogger<AutoSelectService>.Instance;

    public async Task<AutoSelectOutcome> RunAsync(
        IReadOnlyList<string> watchedServices,
        string networkId,
        IProgress<ProgressStep>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<ProgressStep>();

        void Step(string key, string? argument = null, bool done = true)
        {
            var step = new ProgressStep(key, argument, done);
            steps.Add(step);
            progress?.Report(step);
        }

        if (watchedServices.Count == 0)
        {
            return new AutoSelectOutcome { Success = true, BypassNeeded = false, Steps = steps };
        }

        var info = runtime.Current;
        if (info is null)
        {
            Step("step.noEngine");
            return new AutoSelectOutcome { Success = false, AdviceKey = "advice.installEngine", Steps = steps };
        }

        // 1. Measure with the bypass off. Whatever already works needs no fixing, and a connection where
        //    nothing is blocked deserves to be told so instead of being "optimised".
        Step("step.checkingWithoutBypass", null, false);
        await controller.StopAsync(cancellationToken).ConfigureAwait(false);

        var baseline = await probe.ProbeAsync(watchedServices, cancellationToken).ConfigureAwait(false);
        var failing = baseline.Where(v => !v.Reachable).Select(v => v.ServiceId).ToList();

        Step("step.checkingWithoutBypass");

        if (failing.Count == 0)
        {
            _logger.LogInformation("No bypass needed on network {Network}: all {Count} services reachable", networkId, watchedServices.Count);
            Step("step.noBypassNeeded");

            return new AutoSelectOutcome
            {
                Success = true,
                BypassNeeded = false,
                Verdicts = baseline,
                Steps = steps,
            };
        }

        // 2. Try likely candidates against the failing services only, stopping at the first that fixes them.
        var plan = CandidateOrder.Plan(info.Strategies, failing, memory.Read(networkId));

        if (plan.Count == 0)
        {
            Step("step.noCandidates");
            return new AutoSelectOutcome
            {
                Success = false,
                BypassNeeded = true,
                Verdicts = baseline,
                AdviceKey = "advice.installEngine",
                Steps = steps,
            };
        }

        var attempt = 0;

        foreach (var candidateId in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            var strategy = info.Strategies.FirstOrDefault(s => s.Id == candidateId);
            if (strategy is null) continue;

            Step("step.trying", $"{attempt}/{plan.Count}", false);

            if (!await controller.StartAsync(info, strategy, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("Candidate {Candidate} did not start; moving on", candidateId);
                continue;
            }

            if (_settle > TimeSpan.Zero) await Task.Delay(_settle, cancellationToken).ConfigureAwait(false);

            var verdicts = await probe.ProbeAsync(watchedServices, cancellationToken).ConfigureAwait(false);
            var stillFailing = verdicts.Where(v => !v.Reachable).Select(v => v.ServiceId).ToList();

            if (stillFailing.Count == 0)
            {
                _logger.LogInformation("Candidate {Candidate} fixed all {Count} watched services on attempt {Attempt}",
                    candidateId, watchedServices.Count, attempt);

                memory.RememberWorking(networkId, candidateId, failing);
                Step("step.trying", $"{attempt}/{plan.Count}");
                Step("step.fixed");

                return new AutoSelectOutcome
                {
                    Success = true,
                    StrategyId = candidateId,
                    BypassNeeded = true,
                    Verdicts = verdicts,
                    Steps = steps,
                    Attempts = attempt,
                };
            }

            Step("step.trying", $"{attempt}/{plan.Count}");
        }

        // 3. Nothing worked. Stop rather than leave a useless engine running, and say what to try next.
        await controller.StopAsync(cancellationToken).ConfigureAwait(false);

        var exhausted = plan.Count >= info.Strategies.Count(s => s.IsSupported);
        var advice = CandidateOrder.AdviceFor(
            gameFilterEnabled: info.GameFilter.Mode != GameFilterMode.Off,
            ipSetRestricted: info.IpSet == IpSetMode.Loaded,
            everythingTried: exhausted);

        _logger.LogWarning("Automatic selection exhausted {Attempts} candidates on network {Network}", attempt, networkId);
        Step("step.exhausted", attempt.ToString());

        return new AutoSelectOutcome
        {
            Success = false,
            BypassNeeded = true,
            Verdicts = baseline,
            AdviceKey = advice,
            Steps = steps,
            Attempts = attempt,
        };
    }
}

/// <summary>
/// Access to the engine build in use. An interface so selection can be tested without a driver, and so the
/// service can swap the runtime under it after an engine update.
/// </summary>
public interface IFlowsealAdapterProvider
{
    EngineRuntimeInfo? Current { get; }
}
