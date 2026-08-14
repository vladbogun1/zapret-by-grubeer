using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using Zapret.Core;
using Zapret.Core.AutoSelect;
using Zapret.Core.Ipc;

namespace Zapret.Shell;

/// <summary>
/// The only thing the 2.0 interface talks to. It subscribes to the product state and raises intents; it never
/// asks six questions and draws its own conclusions, the way the 1.x window did.
/// <para>
/// The subscription reconnects on its own, so a service restart or an update looks like a brief
/// "unavailable" rather than a window that silently stops updating.
/// </para>
/// </summary>
public sealed class ProductClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = PipeProtocol.Json;

    private readonly CancellationTokenSource _life = new();

    public ProductState State { get; private set; } = ProductState.Unreachable;

    /// <summary>Raised on the UI thread whenever a new state arrives.</summary>
    public event Action<ProductState>? StateChanged;

    public void Start() => _ = SubscribeLoopAsync();

    // ---- intents -----------------------------------------------------------------------------

    public Task<ProductState?> SetUpAsync(IReadOnlyList<string> watched) =>
        SendAsync(IpcOperations.SetUp, new SetUpPayload(watched), TimeSpan.FromMinutes(5));

    public Task<ProductState?> TurnOnAsync() => SendAsync(IpcOperations.TurnOn, null, TimeSpan.FromMinutes(5));

    public Task<ProductState?> TurnOffAsync() => SendAsync(IpcOperations.TurnOff, null, TimeSpan.FromMinutes(1));

    public Task<ProductState?> CancelAsync() => SendAsync(IpcOperations.CancelWork, null, TimeSpan.FromSeconds(30));

    public async Task<ServiceCatalogPayload?> GetCatalogAsync()
    {
        var response = await RequestAsync(IpcOperations.GetServiceCatalog, null, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        return response is { Ok: true } ? PipeProtocol.FromElement<ServiceCatalogPayload>(response.Payload) : null;
    }

    // ---- the expanded surface ----------------------------------------------------------------
    // Thin wrappers over operations the service already exposes. They exist only for the advanced window: the
    // main screen never calls any of them, which is the whole point of the split.

    public Task<StatusPayload?> GetStatusAsync() =>
        QueryAsync<StatusPayload>(IpcOperations.GetStatus, null, TimeSpan.FromSeconds(15));

    public Task<StrategyListPayload?> GetStrategiesAsync() =>
        QueryAsync<StrategyListPayload>(IpcOperations.ListStrategies, null, TimeSpan.FromSeconds(20));

    public Task<TestResultsPayload?> GetTestResultsAsync() =>
        QueryAsync<TestResultsPayload>(IpcOperations.GetTestResults, null, TimeSpan.FromSeconds(20));

    public Task<TestResultsPayload?> RunFullTestAsync() =>
        QueryAsync<TestResultsPayload>(IpcOperations.RunFullTest, null, TimeSpan.FromMinutes(50));

    public Task<OperationResultPayload?> ApplyStrategyAsync(string id) =>
        QueryAsync<OperationResultPayload>(IpcOperations.ApplyStrategy, new IdPayload(id), TimeSpan.FromMinutes(2));

    public Task<OperationResultPayload?> SetServiceEnabledAsync(string id, bool enabled) =>
        QueryAsync<OperationResultPayload>(IpcOperations.SetServiceEnabled, new ServiceTogglePayload(id, enabled), TimeSpan.FromMinutes(2));

    public Task<OperationResultPayload?> AddCustomServiceAsync(string id, IReadOnlyList<string> domains, string? checkUrl) =>
        QueryAsync<OperationResultPayload>(IpcOperations.AddCustomService, new CustomServicePayload(id, domains, checkUrl), TimeSpan.FromMinutes(2));

    public Task<OperationResultPayload?> RemoveCustomServiceAsync(string id) =>
        QueryAsync<OperationResultPayload>(IpcOperations.RemoveCustomService, new IdPayload(id), TimeSpan.FromMinutes(2));

    public Task<OperationResultPayload?> SetGameFilterAsync(Zapret.Core.Model.GameFilterMode mode) =>
        QueryAsync<OperationResultPayload>(IpcOperations.SetGameFilter, new GameFilterPayload(mode), TimeSpan.FromMinutes(2));

    public Task<OperationResultPayload?> SetIpSetModeAsync(Zapret.Core.Model.IpSetMode mode) =>
        QueryAsync<OperationResultPayload>(IpcOperations.SetIpSetMode, new IpSetPayload(mode), TimeSpan.FromMinutes(2));

    public Task<OperationResultPayload?> UpdateIpSetListAsync() =>
        QueryAsync<OperationResultPayload>(IpcOperations.UpdateIpSetList, null, TimeSpan.FromMinutes(2));

    public Task<OperationResultPayload?> ApplyHostsAsync() =>
        QueryAsync<OperationResultPayload>(IpcOperations.ApplyManagedHosts, null, TimeSpan.FromMinutes(2));

    public Task<OperationResultPayload?> RemoveHostsAsync() =>
        QueryAsync<OperationResultPayload>(IpcOperations.RemoveManagedHosts, null, TimeSpan.FromMinutes(2));

    public async Task<string> GetLogTailAsync(string source, int lines = 300)
    {
        var payload = await QueryAsync<LogTailPayload>(IpcOperations.GetLogTail, new LogTailPayload(source, lines), TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        return payload?.Content ?? string.Empty;
    }

    private async Task<T?> QueryAsync<T>(string operation, object? payload, TimeSpan timeout)
    {
        var response = await RequestAsync(operation, payload, timeout).ConfigureAwait(false);
        return response is { Ok: true } ? PipeProtocol.FromElement<T>(response.Payload) : default;
    }

    /// <summary>True when the caller may change things; the advanced window disables the rest rather than lying.</summary>
    public async Task<bool> CanModifyAsync() => (await GetStatusAsync().ConfigureAwait(false))?.IsElevatedCaller ?? false;

    private async Task<ProductState?> SendAsync(string operation, object? payload, TimeSpan timeout)
    {
        var response = await RequestAsync(operation, payload, timeout).ConfigureAwait(false);
        if (response is not { Ok: true }) return null;

        var state = PipeProtocol.FromElement<ProductState>(response.Payload);
        if (state is not null) Publish(state);

        return state;
    }

    // ---- transport ---------------------------------------------------------------------------

    private async Task<IpcResponse?> RequestAsync(string operation, object? payload, TimeSpan timeout)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        cancellation.CancelAfter(timeout);

        try
        {
            await using var pipe = new NamedPipeClientStream(".", AppPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(3000, cancellation.Token).ConfigureAwait(false);

            await PipeProtocol.WriteMessageAsync(pipe, new IpcRequest
            {
                Operation = operation,
                Payload = payload is null ? null : PipeProtocol.ToElement(payload),
            }, cancellation.Token).ConfigureAwait(false);

            var raw = await PipeProtocol.ReadMessageAsync(pipe, cancellation.Token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(raw) ? null : JsonSerializer.Deserialize<IpcResponse>(raw, Json);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or JsonException)
        {
            Publish(ProductState.Unreachable);
            return null;
        }
    }

    /// <summary>
    /// Holds one connection open and applies every state the service pushes. On any failure it waits and
    /// reconnects: the interface must recover from a service restart without the user doing anything.
    /// </summary>
    private async Task SubscribeLoopAsync()
    {
        while (!_life.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", AppPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(3000, _life.Token).ConfigureAwait(false);

                await PipeProtocol.WriteMessageAsync(pipe, new IpcRequest { Operation = IpcOperations.SubscribeState }, _life.Token)
                    .ConfigureAwait(false);

                while (!_life.IsCancellationRequested)
                {
                    var raw = await PipeProtocol.ReadMessageAsync(pipe, _life.Token).ConfigureAwait(false);
                    if (raw is null) break;

                    var response = JsonSerializer.Deserialize<IpcResponse>(raw, Json);
                    var state = response is { Ok: true } ? PipeProtocol.FromElement<ProductState>(response.Payload) : null;

                    if (state is not null) Publish(state);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or JsonException)
            {
                Publish(ProductState.Unreachable);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), _life.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Publish(ProductState state)
    {
        State = state;

        var application = System.Windows.Application.Current;
        if (application is null) return;

        application.Dispatcher.Invoke(() => StateChanged?.Invoke(state));
    }

    public void Dispose()
    {
        _life.Cancel();
        _life.Dispose();
    }
}
