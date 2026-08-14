using Zapret.Core.Model;

namespace Zapret.Core.AutoSelect;

/// <summary>
/// What the product remembers about strategies, so selection gets faster every time instead of starting from
/// scratch. Keys are opaque: a network fingerprint and a service id.
/// </summary>
public sealed record SelectionMemory
{
    /// <summary>Strategy that last worked on this connection.</summary>
    public string? LastWorkingOnNetwork { get; init; }

    /// <summary>Strategy that last fixed a particular service, whatever the connection.</summary>
    public IReadOnlyDictionary<string, string> LastWorkingPerService { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Strategies already tried and failed in the current attempt; never offered twice.</summary>
    public IReadOnlyCollection<string> Excluded { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Decides which strategies to try, and in what order, when the product is fixing things by itself.
/// <para>
/// This is the heart of §4 of docs/nextgen-ux.md: the 1.x product measured all 21 strategies for five minutes
/// and then asked the user to choose. Here the product tries the most likely candidates against the services
/// the user actually named and stops at the first that works — usually one or two attempts.
/// </para>
/// </summary>
public static class CandidateOrder
{
    /// <summary>
    /// How many candidates a single automatic attempt may try before giving up and asking for help. Each
    /// candidate costs an engine restart plus a probe, so the ceiling exists to keep Repairing short enough
    /// that a user will wait through it.
    /// </summary>
    public const int MaxAutomaticAttempts = 6;

    /// <summary>
    /// Best guesses first:
    /// <list type="number">
    /// <item>what already worked on this connection — by far the most likely to work again;</item>
    /// <item>what fixed the failing services elsewhere — the blocking is usually per-service, not per-ISP;</item>
    /// <item>upstream's own order, lower variants first, because those are the conservative ones.</item>
    /// </list>
    /// Unsupported strategies are never offered, and nothing is offered twice.
    /// </summary>
    public static IReadOnlyList<string> Build(
        IReadOnlyList<StrategyDescriptor> catalog,
        IReadOnlyList<string> failingServices,
        SelectionMemory memory)
    {
        var usable = catalog
            .Where(s => s.IsSupported)
            .Select(s => s.Id)
            .ToList();

        var excluded = new HashSet<string>(memory.Excluded, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        void Consider(string? id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (excluded.Contains(id)) return;
            if (!usable.Contains(id, StringComparer.OrdinalIgnoreCase)) return;
            if (ordered.Contains(id, StringComparer.OrdinalIgnoreCase)) return;

            ordered.Add(id);
        }

        Consider(memory.LastWorkingOnNetwork);

        // Services in the order the user's own failures came in, so the most relevant memory wins first.
        foreach (var service in failingServices)
        {
            if (memory.LastWorkingPerService.TryGetValue(service, out var remembered)) Consider(remembered);
        }

        foreach (var id in usable) Consider(id);

        return ordered;
    }

    /// <summary>
    /// The plan for one automatic attempt: the ordered candidates, capped so Repairing stays short. Returning
    /// fewer than the cap is normal and means the catalogue is small or memory already narrowed it down.
    /// </summary>
    public static IReadOnlyList<string> Plan(
        IReadOnlyList<StrategyDescriptor> catalog,
        IReadOnlyList<string> failingServices,
        SelectionMemory memory,
        int limit = MaxAutomaticAttempts) =>
        Build(catalog, failingServices, memory).Take(Math.Max(1, limit)).ToList();

    /// <summary>
    /// What to tell the user when every candidate failed. The advice must name something a person can do, not
    /// a setting they cannot judge — a dead end is a product failure, not a user error (§6).
    /// </summary>
    public static string AdviceFor(bool gameFilterEnabled, bool ipSetRestricted, bool everythingTried) =>
        !gameFilterEnabled ? "advice.tryGameFilter"
        : ipSetRestricted ? "advice.widenIpSet"
        : everythingTried ? "advice.sendReport"
        : "advice.tryFullSweep";
}
