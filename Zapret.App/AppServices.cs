using System.Windows.Controls;
using Wpf.Ui.Abstractions;
using Zapret.Core;
using Zapret.Core.Engine;
using Zapret.Core.Ipc;
using Zapret.Core.Model;

namespace Zapret.App;

/// <summary>
/// The UI's view of the privileged service. Wraps every call so a stopped service becomes read-only
/// mode with an explanation rather than an exception (ADR-0002).
/// </summary>
public sealed class ManagerClient
{
    private readonly ZapretClient _pipe = new();

    public StatusPayload? Status { get; private set; }

    public bool ServiceAvailable { get; private set; }

    public string? UnavailableReason { get; private set; }

    /// <summary>True when the service answers and the caller may change things.</summary>
    public bool CanModify => ServiceAvailable && Status?.IsElevatedCaller == true;

    public event Action? Changed;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var response = await _pipe.SendAsync(IpcOperations.GetStatus, null, TimeSpan.FromSeconds(6), cancellationToken)
            .ConfigureAwait(false);

        if (response.Ok)
        {
            Status = PipeProtocol.FromElement<StatusPayload>(response.Payload);
            ServiceAvailable = true;
            UnavailableReason = null;
        }
        else
        {
            ServiceAvailable = false;
            UnavailableReason = response.Error;
        }

        Changed?.Invoke();
    }

    public Task<StrategyListPayload?> GetStrategiesAsync(CancellationToken cancellationToken = default) =>
        _pipe.QueryAsync<StrategyListPayload>(IpcOperations.ListStrategies, null, null, cancellationToken);

    public Task<UserListPayload?> GetUserListAsync(string name, CancellationToken cancellationToken = default) =>
        _pipe.QueryAsync<UserListPayload>(IpcOperations.GetUserList, new UserListPayload(name), null, cancellationToken);

    public Task<OperationOutcome> StartAsync(CancellationToken ct = default) => InvokeAsync(IpcOperations.StartEngine, null, ct);

    public Task<OperationOutcome> StopAsync(CancellationToken ct = default) => InvokeAsync(IpcOperations.StopEngine, null, ct);

    public Task<OperationOutcome> ApplyStrategyAsync(string id, CancellationToken ct = default) =>
        InvokeAsync(IpcOperations.ApplyStrategy, new IdPayload(id), ct, TimeSpan.FromMinutes(2));

    public Task<OperationOutcome> SetRunModeAsync(EngineRunMode mode, CancellationToken ct = default) =>
        InvokeAsync(IpcOperations.SetRunMode, new RunModePayload(mode), ct, TimeSpan.FromMinutes(2));

    public Task<OperationOutcome> SetAutostartAsync(bool enabled, CancellationToken ct = default) =>
        InvokeAsync(IpcOperations.SetAutostart, new AutostartPayload(enabled), ct);

    public Task<OperationOutcome> SetGameFilterAsync(GameFilterMode mode, CancellationToken ct = default) =>
        InvokeAsync(IpcOperations.SetGameFilter, new GameFilterPayload(mode), ct, TimeSpan.FromMinutes(2));

    public Task<OperationOutcome> SetIpSetModeAsync(IpSetMode mode, CancellationToken ct = default) =>
        InvokeAsync(IpcOperations.SetIpSetMode, new IpSetPayload(mode), ct, TimeSpan.FromMinutes(2));

    public Task<OperationOutcome> SaveUserListAsync(string name, string content, CancellationToken ct = default) =>
        InvokeAsync(IpcOperations.SaveUserList, new UserListPayload(name, content), ct, TimeSpan.FromMinutes(2));

    public Task<OperationOutcome> ApplyManagedHostsAsync(CancellationToken ct = default) =>
        InvokeAsync(IpcOperations.ApplyManagedHosts, null, ct);

    public Task<OperationOutcome> RemoveManagedHostsAsync(CancellationToken ct = default) =>
        InvokeAsync(IpcOperations.RemoveManagedHosts, null, ct);

    public Task<OperationOutcome> UpdateIpSetListAsync(CancellationToken ct = default) =>
        InvokeAsync(IpcOperations.UpdateIpSetList, null, ct, TimeSpan.FromMinutes(2));

    public Task<OperationOutcome> RunStrategyTestsAsync(CancellationToken ct = default) =>
        InvokeAsync(IpcOperations.RunStrategyTests, null, ct, TimeSpan.FromMinutes(20));

    public async Task<string> GetLogTailAsync(string source, int lines = 300, CancellationToken ct = default)
    {
        var payload = await _pipe.QueryAsync<LogTailPayload>(IpcOperations.GetLogTail, new LogTailPayload(source, lines), null, ct)
            .ConfigureAwait(false);

        return payload?.Content ?? string.Empty;
    }

    public async Task<EngineUpdatePayload?> InstallEngineAsync(string tag, CancellationToken ct = default)
    {
        var response = await _pipe
            .SendAsync(IpcOperations.InstallEngine, new InstallEnginePayload(tag), TimeSpan.FromMinutes(20), ct)
            .ConfigureAwait(false);

        return response.Ok ? PipeProtocol.FromElement<EngineUpdatePayload>(response.Payload) : null;
    }

    public async Task<EngineUpdatePayload?> RollBackEngineAsync(CancellationToken ct = default)
    {
        var response = await _pipe
            .SendAsync(IpcOperations.RollBackEngine, null, TimeSpan.FromMinutes(5), ct)
            .ConfigureAwait(false);

        return response.Ok ? PipeProtocol.FromElement<EngineUpdatePayload>(response.Payload) : null;
    }

    private async Task<OperationOutcome> InvokeAsync(string operation, object? payload, CancellationToken ct, TimeSpan? timeout = null)
    {
        var response = await _pipe.SendAsync(operation, payload, timeout, ct).ConfigureAwait(false);

        if (!response.Ok)
        {
            return new OperationOutcome(false, response.Error ?? "The operation failed.", response.Code);
        }

        var result = PipeProtocol.FromElement<OperationResultPayload>(response.Payload);
        await RefreshAsync(ct).ConfigureAwait(false);

        return new OperationOutcome(result?.Success ?? true, result?.Message, IpcErrorCode.None);
    }
}

public sealed record OperationOutcome(bool Success, string? Message, IpcErrorCode Code)
{
    public bool NeedsElevation => Code == IpcErrorCode.Unauthorized;
    public bool ServiceMissing => Code == IpcErrorCode.ServiceUnavailable;
}

/// <summary>
/// Hands page instances to WPF UI's navigation. Pages are singletons so navigating back to one keeps
/// its state, which is what a settings-style application should do.
/// </summary>
public sealed class PageProvider(IReadOnlyDictionary<Type, Func<Page>> factories) : INavigationViewPageProvider
{
    private readonly Dictionary<Type, Page> _instances = new();

    public object? GetPage(Type pageType)
    {
        if (_instances.TryGetValue(pageType, out var existing)) return existing;
        if (!factories.TryGetValue(pageType, out var factory)) return null;

        var page = factory();
        _instances[pageType] = page;
        return page;
    }
}
