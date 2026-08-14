using Zapret.Core;
using Zapret.Core.AutoSelect;
using Zapret.Core.Engine;
using Zapret.Core.Model;
using Zapret.Core.Services;
using Zapret.Core.SystemIntegration;

namespace Zapret.Service;

/// <summary>
/// Owns the product's behaviour: it decides what stage the user is in, keeps things working without being
/// asked, and pushes one <see cref="ProductState"/> to whoever is listening.
/// <para>
/// This is the piece that makes the 2.0 interface possible. In 1.x the window polled six queries and drew its
/// own conclusions about what they meant; here the service concludes and the window renders
/// (docs/nextgen-ux.md §8).
/// </para>
/// </summary>
public sealed class ProductOrchestrator(
    ISettingsStore settings,
    EngineHost host,
    IServiceProbe probe,
    SelectionMemoryStore memory,
    ManagerEventLog events,
    ILogger<ProductOrchestrator> logger) : IDisposable
{
    private readonly HealthMonitor _monitor = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ProductState _state = new() { Stage = ProductStage.FirstRun };
    private CancellationTokenSource? _work;

    /// <summary>Raised whenever the state changes, so listeners never have to poll.</summary>
    public event Action<ProductState>? StateChanged;

    public ProductState State => _state;

    // ---- lifecycle ---------------------------------------------------------------------------

    /// <summary>
    /// Works out where the user stands without touching anything. Called at startup and after any change that
    /// could alter the answer.
    /// </summary>
    public void Recompute()
    {
        var current = settings.Read();
        var status = host.GetStatus(callerIsAdministrator: true);

        var stage =
            !current.OnboardingCompleted ? ProductStage.FirstRun
            : current.TurnedOffByUser ? ProductStage.Off
            : status.EngineVersion is null ? ProductStage.FirstRun
            : _monitor.IsDegraded ? ProductStage.Degraded
            : status.EngineStatus == EngineStatus.Running ? ProductStage.Working
            : ProductStage.Off;

        // The conclusion about this connection is remembered, so a restart does not downgrade the precise
        // «no bypass needed» to the vaguer «everything works».
        var network = NetworkIdentity.Detect();
        var bypassNeeded = current.NetworkBypassNeeded.TryGetValue(network.Id, out var known) ? known : (bool?)null;

        Publish(_state with
        {
            Stage = stage,
            WatchedServices = current.WatchedServices,
            StrategyId = status.StrategyId,
            EngineVersion = status.EngineVersion,

            // Only a running engine has an uptime; anything else reporting one is a lie the user can see.
            RunningSinceUtc = status.EngineStatus == EngineStatus.Running ? status.StartedUtc : null,
            BypassNeeded = bypassNeeded,
            Steps = Array.Empty<ProgressStep>(),
            CanCancel = false,
        });
    }

    /// <summary>
    /// The whole of onboarding: remember what the user cares about, make it work, and say what happened. One
    /// call, because from the user's side it is one decision (docs/nextgen-ux.md §5).
    /// </summary>
    public async Task<ProductState> SetUpAsync(IReadOnlyList<string> watchedServices, CancellationToken cancellationToken)
    {
        settings.Update(s =>
        {
            s.WatchedServices = watchedServices.ToList();
            s.EnabledServices = watchedServices.ToList();
            s.TurnedOffByUser = false;
            s.OnboardingCompleted = true;
        });

        // Switching services on also writes their domains into the user list, so the engine covers them.
        foreach (var id in watchedServices)
        {
            await host.SetServiceEnabledAsync(id, true, cancellationToken).ConfigureAwait(false);
        }

        return await SelectAsync(watchedServices, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProductState> TurnOnAsync(CancellationToken cancellationToken)
    {
        settings.Update(s => s.TurnedOffByUser = false);

        var watched = settings.Read().WatchedServices;
        return await SelectAsync(watched, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The user's explicit choice. Nothing is broken, and the product must not quietly restart.</summary>
    public async Task<ProductState> TurnOffAsync(CancellationToken cancellationToken)
    {
        CancelWork();
        settings.Update(s => s.TurnedOffByUser = true);

        _monitor.Reset();
        await host.StopAsync(cancellationToken).ConfigureAwait(false);

        // Nothing is running, so nothing may claim an uptime. Leaving this set made the window show
        // «работает столько-то» next to «защита выключена», which reads as a product that did not obey.
        Publish(_state with
        {
            Stage = ProductStage.Off,
            Steps = Array.Empty<ProgressStep>(),
            CanCancel = false,
            Verdicts = Array.Empty<ServiceVerdict>(),
            RunningSinceUtc = null,
        });

        return _state;
    }

    /// <summary>Cancels a repair and leaves whatever was working before it started.</summary>
    public void Cancel()
    {
        CancelWork();
        Recompute();
    }

    // ---- selection and repair ----------------------------------------------------------------

    private async Task<ProductState> SelectAsync(IReadOnlyList<string> watched, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancelWork();
            _work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var steps = new List<ProgressStep>();
            var progress = new Progress<ProgressStep>(step =>
            {
                // A step already announced as pending is replaced when it completes, so the list reads as
                // history rather than as duplicates.
                steps.RemoveAll(s => s.MessageKey == step.MessageKey && s.Argument == step.Argument && !s.Done);
                steps.Add(step);

                Publish(_state with { Stage = ProductStage.Preparing, Steps = steps.ToList(), CanCancel = true });
            });

            var network = NetworkIdentity.Detect();
            memory.ClearExclusions(network.Id);

            var selector = new AutoSelectService(host, host.ActiveController, probe, memory);
            var outcome = await selector.RunAsync(watched, network.Id, progress, _work.Token).ConfigureAwait(false);

            _monitor.Reset();

            if (outcome.Success)
            {
                settings.Update(s => s.NetworkBypassNeeded[network.Id] = outcome.BypassNeeded);
            }

            var stage = outcome.Success
                ? outcome.BypassNeeded ? ProductStage.Working : ProductStage.Working
                : ProductStage.Stuck;

            events.Add(
                outcome.Success ? ManagerEventLevel.Success : ManagerEventLevel.Warning,
                outcome.Success ? ManagerEvents.StrategyApplied : ManagerEvents.StrategyUnavailable,
                outcome.StrategyId);

            Publish(new ProductState
            {
                Stage = stage,
                WatchedServices = watched,
                Verdicts = outcome.Verdicts,
                Steps = outcome.Steps,
                StrategyId = outcome.StrategyId,
                EngineVersion = host.Runtime?.Version.Raw,
                RunningSinceUtc = host.ActiveController.State.StartedUtc,
                BypassNeeded = outcome.BypassNeeded,
                AdviceKey = outcome.AdviceKey,
                CanCancel = false,
            });

            return _state;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// One round of monitoring. Called on a timer by the hosted service; acts only on a transition, and repairs
    /// without being asked, which is what removes the manual test buttons.
    /// </summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var current = settings.Read();

        if (!current.OnboardingCompleted || current.TurnedOffByUser) return;
        if (current.WatchedServices.Count == 0) return;
        if (host.ActiveController.State.Status != EngineStatus.Running && _state.BypassNeeded != false) return;

        var verdicts = await probe.ProbeAsync(current.WatchedServices, cancellationToken).ConfigureAwait(false);
        var verdict = _monitor.Observe(verdicts);

        Publish(_state with { Verdicts = verdicts });

        switch (verdict)
        {
            case HealthVerdict.Degraded:
                logger.LogWarning("Sustained failure of {Services}; repairing", string.Join(", ", _monitor.Failing));
                events.Add(ManagerEventLevel.Warning, ManagerEvents.EngineFaulted, string.Join(", ", _monitor.Failing));

                Publish(_state with { Stage = ProductStage.Repairing, CanCancel = true });
                await SelectAsync(current.WatchedServices, cancellationToken).ConfigureAwait(false);
                break;

            case HealthVerdict.Recovered:
                logger.LogInformation("Services recovered without a repair");
                Publish(_state with { Stage = ProductStage.Working });
                break;
        }
    }

    private void CancelWork()
    {
        try
        {
            _work?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already gone; nothing to cancel.
        }

        _work?.Dispose();
        _work = null;
    }

    private void Publish(ProductState state)
    {
        _state = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        CancelWork();
        _gate.Dispose();
    }
}
