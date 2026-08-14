using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zapret.Core.Ipc;

/// <summary>
/// The UI side of the channel. Every call is a short-lived connection, so a stopped or restarted
/// service never leaves the UI holding a dead pipe — it just reports read-only mode.
/// </summary>
public sealed class ZapretClient(string pipeName = AppPaths.PipeName, ILogger<ZapretClient>? logger = null)
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    private readonly ILogger _logger = logger ?? NullLogger<ZapretClient>.Instance;

    public async Task<IpcResponse> SendAsync(
        string operation,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));

        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, linked.Token).ConfigureAwait(false);

            var request = new IpcRequest
            {
                Operation = operation,
                Payload = payload is null ? null : PipeProtocol.ToElement(payload),
            };

            await PipeProtocol.WriteMessageAsync(pipe, request, linked.Token).ConfigureAwait(false);

            var raw = await PipeProtocol.ReadMessageAsync(pipe, linked.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return IpcResponse.Failure(IpcErrorCode.ServiceUnavailable, "The service closed the connection without answering.");
            }

            var response = JsonSerializer.Deserialize<IpcResponse>(raw, PipeProtocol.Json);
            if (response is null)
            {
                return IpcResponse.Failure(IpcErrorCode.InvalidPayload, "The service sent a response that could not be read.");
            }

            if (response.ProtocolVersion != IpcRequest.CurrentProtocolVersion)
            {
                return IpcResponse.Failure(IpcErrorCode.ProtocolMismatch,
                    $"{AppPaths.DisplayName} and its background service are different versions. Restart is required.");
            }

            return response;
        }
        catch (TimeoutException)
        {
            return Unavailable(operation, null);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return Unavailable(operation, ex);
        }
    }

    public async Task<T?> QueryAsync<T>(
        string operation,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(operation, payload, timeout, cancellationToken).ConfigureAwait(false);
        return response is { Ok: true } ? PipeProtocol.FromElement<T>(response.Payload) : default;
    }

    /// <summary>Cheap liveness probe used to decide between full and read-only UI.</summary>
    public async Task<bool> IsServiceReachableAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(IpcOperations.GetStatus, null, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        return response.Ok;
    }

    private IpcResponse Unavailable(string operation, Exception? ex)
    {
        _logger.LogDebug(ex, "The service could not be reached for {Operation}", operation);
        return IpcResponse.Failure(IpcErrorCode.ServiceUnavailable,
            $"The {AppPaths.DisplayName} background service is not running.");
    }
}
