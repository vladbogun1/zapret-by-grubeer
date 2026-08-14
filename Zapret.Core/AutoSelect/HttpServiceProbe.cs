using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zapret.Core.Services;

namespace Zapret.Core.AutoSelect;

/// <summary>
/// Probes exactly the services a user named, using each service's own check URL from the catalogue.
/// <para>
/// Deliberately narrow: the 1.x product probed a fixed list of four targets and called it a service check.
/// Here the question is the user's — "does the thing I said I use work" — so the targets come from their
/// selection, and a service with no check URL is honestly reported as unmeasurable rather than assumed fine.
/// </para>
/// </summary>
public sealed class HttpServiceProbe(
    HttpClient http,
    Func<IReadOnlyList<ServiceDefinition>> catalogue,
    ILogger<HttpServiceProbe>? logger = null) : IServiceProbe
{
    /// <summary>
    /// Per-request ceiling. Short on purpose: a blocked connection usually fails fast, and a slow answer is
    /// still an answer the user would rather have now than in ten seconds.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    private readonly ILogger _logger = logger ?? NullLogger<HttpServiceProbe>.Instance;

    public async Task<IReadOnlyList<ServiceVerdict>> ProbeAsync(
        IReadOnlyList<string> serviceIds,
        CancellationToken cancellationToken)
    {
        if (serviceIds.Count == 0) return Array.Empty<ServiceVerdict>();

        var known = catalogue().ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

        // In parallel: four sequential six-second timeouts would make the product feel broken while it works.
        var probes = serviceIds.Select(async id =>
        {
            if (!known.TryGetValue(id, out var service) || string.IsNullOrWhiteSpace(service.CheckUrl))
            {
                // No way to measure it, so no claim about it. Reported unreachable rather than silently "fine":
                // pretending success here would be the product lying about the one thing it promises.
                _logger.LogDebug("Service {Service} has no check URL and cannot be measured", id);
                return new ServiceVerdict(id, false, null, DateTimeOffset.UtcNow);
            }

            return await ProbeOneAsync(id, service.CheckUrl!, cancellationToken).ConfigureAwait(false);
        });

        return await Task.WhenAll(probes).ConfigureAwait(false);
    }

    private async Task<ServiceVerdict> ProbeOneAsync(string id, string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            stopwatch.Stop();

            // Any answer at all means the connection was not torn down: even a 403 proves reachability, which
            // is the only thing being measured here.
            return new ServiceVerdict(id, true, (int)stopwatch.ElapsedMilliseconds, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            stopwatch.Stop();

            if (cancellationToken.IsCancellationRequested) throw;

            _logger.LogDebug(ex, "Service {Service} is not reachable at {Url}", id, url);
            return new ServiceVerdict(id, false, null, DateTimeOffset.UtcNow);
        }
    }
}

/// <summary>
/// Remembers what worked, in the manager's own settings. Keyed by the one-way network fingerprint, so the
/// stored value cannot be turned back into where the user was.
/// </summary>
public sealed class SelectionMemoryStore(ISettingsStore settings) : ISelectionMemoryStore
{
    /// <summary>Candidates ruled out during the current attempt; per-process, not persisted.</summary>
    private readonly Dictionary<string, HashSet<string>> _excluded = new(StringComparer.OrdinalIgnoreCase);

    public SelectionMemory Read(string networkId)
    {
        var current = settings.Read();

        return new SelectionMemory
        {
            LastWorkingOnNetwork = current.NetworkStrategies.TryGetValue(networkId, out var network) ? network : null,
            LastWorkingPerService = current.ServiceStrategies,
            Excluded = _excluded.TryGetValue(networkId, out var set) ? set.ToList() : Array.Empty<string>(),
        };
    }

    public void RememberWorking(string networkId, string strategyId, IReadOnlyList<string> fixedServices)
    {
        settings.Update(s =>
        {
            s.NetworkStrategies[networkId] = strategyId;

            // Blocking is usually per service, so what fixed a service is worth remembering across networks.
            foreach (var service in fixedServices) s.ServiceStrategies[service] = strategyId;
        });

        // A success ends the attempt, so nothing stays ruled out for next time.
        _excluded.Remove(networkId);
    }

    /// <summary>Rules a candidate out for the rest of the current attempt.</summary>
    public void Exclude(string networkId, string strategyId)
    {
        if (!_excluded.TryGetValue(networkId, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _excluded[networkId] = set;
        }

        set.Add(strategyId);
    }

    /// <summary>Starts a fresh attempt, so previously ruled-out candidates are considered again.</summary>
    public void ClearExclusions(string networkId) => _excluded.Remove(networkId);
}
