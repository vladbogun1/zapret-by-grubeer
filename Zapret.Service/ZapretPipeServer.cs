using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Zapret.Core;
using Zapret.Core.Engine;
using Zapret.Core.Ipc;
using Zapret.Core.Model;

namespace Zapret.Service;

/// <summary>
/// The named-pipe endpoint. Any signed-in user may query state; changing anything requires a caller in
/// the local Administrators group, verified by impersonating the client (ADR-0002).
/// </summary>
public sealed class ZapretPipeServer(EngineHost host, ILogger<ZapretPipeServer> logger) : BackgroundService
{
    private const int ConcurrentInstances = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initialization must not delay the endpoint: the UI has to be able to connect immediately.
        _ = Task.Run(async () =>
        {
            try
            {
                await host.InitializeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Engine initialization failed");
            }
        }, stoppingToken);

        var loops = Enumerable
            .Range(0, ConcurrentInstances)
            .Select(index => AcceptLoopAsync(index, stoppingToken))
            .ToArray();

        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(int index, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);

                await HandleConnectionAsync(pipe, stoppingToken).ConfigureAwait(false);

                if (pipe.IsConnected) pipe.Disconnect();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Pipe listener {Index} recovered from an error", index);
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        // Read-only clients still need write access to send their request; authorization is per operation.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            AppPaths.PipeName,
            PipeDirection.InOut,
            ConcurrentInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        var raw = await PipeProtocol.ReadMessageAsync(pipe, stoppingToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw)) return;

        IpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<IpcRequest>(raw, PipeProtocol.Json);
        }
        catch (JsonException)
        {
            await Respond(pipe, IpcResponse.Failure(IpcErrorCode.InvalidPayload, "The request could not be read."), stoppingToken).ConfigureAwait(false);
            return;
        }

        if (request is null || string.IsNullOrEmpty(request.Operation))
        {
            await Respond(pipe, IpcResponse.Failure(IpcErrorCode.InvalidPayload, "The request was empty."), stoppingToken).ConfigureAwait(false);
            return;
        }

        if (request.ProtocolVersion != IpcRequest.CurrentProtocolVersion)
        {
            await Respond(pipe, IpcResponse.Failure(IpcErrorCode.ProtocolMismatch,
                $"{AppPaths.DisplayName} and its background service are different versions. Restart is required."), stoppingToken).ConfigureAwait(false);
            return;
        }

        var caller = Identify(pipe);

        if (IpcOperations.RequiresAdministrator(request.Operation) && !caller.IsAdministrator)
        {
            logger.LogWarning("Denied {Operation} to {User} ({Sid}): administrator rights are required",
                request.Operation, caller.Name, caller.Sid);

            await Respond(pipe, IpcResponse.Failure(IpcErrorCode.Unauthorized,
                "This action requires administrator rights."), stoppingToken).ConfigureAwait(false);
            return;
        }

        if (IpcOperations.RequiresAdministrator(request.Operation))
        {
            logger.LogInformation("{Operation} requested by {User} ({Sid})", request.Operation, caller.Name, caller.Sid);
        }

        IpcResponse response;
        try
        {
            response = await DispatchAsync(request, caller, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Operation} failed", request.Operation);
            response = IpcResponse.Failure(IpcErrorCode.OperationFailed, ex.Message);
        }

        await Respond(pipe, response, stoppingToken).ConfigureAwait(false);
    }

    private async Task<IpcResponse> DispatchAsync(IpcRequest request, CallerIdentity caller, CancellationToken cancellationToken)
    {
        switch (request.Operation)
        {
            case IpcOperations.GetStatus:
                return Ok(host.GetStatus(caller.IsAdministrator));

            case IpcOperations.ListStrategies:
                return Ok(host.ListStrategies());

            case IpcOperations.GetUserList:
            {
                var payload = Require<UserListPayload>(request);
                var content = host.GetUserList(payload.Name);
                return content is null
                    ? IpcResponse.Failure(IpcErrorCode.InvalidPayload, $"'{payload.Name}' is not a user-editable list.")
                    : Ok(new UserListPayload(payload.Name, content));
            }

            case IpcOperations.GetLogTail:
            {
                var payload = Require<LogTailPayload>(request);
                return Ok(payload with { Content = host.GetLogTail(payload.Source, payload.Lines) });
            }

            case IpcOperations.ProbeServices:
                return Ok(await host.ProbeServicesAsync(cancellationToken).ConfigureAwait(false));

            case IpcOperations.GetEvents:
                return Ok(host.GetEvents(20));

            case IpcOperations.GetTestResults:
                return Ok(host.GetTestResults());

            case IpcOperations.GetServiceCatalog:
                return Ok(host.GetServiceCatalog());

            case IpcOperations.SetServiceEnabled:
            {
                var payload = Require<ServiceTogglePayload>(request);
                return Ok(await host.SetServiceEnabledAsync(payload.Id, payload.Enabled, cancellationToken).ConfigureAwait(false));
            }

            case IpcOperations.AddCustomService:
            {
                var payload = Require<CustomServicePayload>(request);
                return Ok(await host.AddCustomServiceAsync(payload.Id, payload.Domains, payload.CheckUrl, cancellationToken).ConfigureAwait(false));
            }

            case IpcOperations.RemoveCustomService:
                return Ok(await host.RemoveCustomServiceAsync(Require<IdPayload>(request).Id, cancellationToken).ConfigureAwait(false));

            case IpcOperations.RunFullTest:
                return Ok(await host.RunFullTestAsync(cancellationToken).ConfigureAwait(false));

            case IpcOperations.ApplyBestStrategy:
                return Ok(await host.ApplyBestStrategyAsync(cancellationToken).ConfigureAwait(false));

            case IpcOperations.StartEngine:
                return Ok(await host.StartAsync(null, cancellationToken).ConfigureAwait(false));

            case IpcOperations.StopEngine:
                return Ok(await host.StopAsync(cancellationToken).ConfigureAwait(false));

            case IpcOperations.ApplyStrategy:
                return Ok(await host.StartAsync(Require<IdPayload>(request).Id, cancellationToken).ConfigureAwait(false));

            case IpcOperations.SetRunMode:
                return Ok(await host.SetRunModeAsync(Require<RunModePayload>(request).Mode, cancellationToken).ConfigureAwait(false));

            case IpcOperations.SetAutostart:
                return Ok(host.SetAutostart(Require<AutostartPayload>(request).Enabled));

            case IpcOperations.SetGameFilter:
                return Ok(await host.SetGameFilterAsync(Require<GameFilterPayload>(request).Mode, cancellationToken).ConfigureAwait(false));

            case IpcOperations.SetIpSetMode:
                return Ok(await host.SetIpSetModeAsync(Require<IpSetPayload>(request).Mode, cancellationToken).ConfigureAwait(false));

            case IpcOperations.SaveUserList:
            {
                var payload = Require<UserListPayload>(request);
                return Ok(await host.SaveUserListAsync(payload.Name, payload.Content ?? string.Empty, cancellationToken).ConfigureAwait(false));
            }

            case IpcOperations.InstallEngine:
                return Ok(await host.InstallEngineAsync(Require<InstallEnginePayload>(request).Tag, cancellationToken).ConfigureAwait(false));

            case IpcOperations.RollBackEngine:
                return Ok(await host.RollBackEngineAsync(cancellationToken).ConfigureAwait(false));

            case IpcOperations.UpdateIpSetList:
                return Ok(await host.UpdateIpSetListAsync(cancellationToken).ConfigureAwait(false));

            case IpcOperations.ApplyManagedHosts:
                return Ok(await host.ApplyManagedHostsAsync(cancellationToken).ConfigureAwait(false));

            case IpcOperations.RemoveManagedHosts:
                return Ok(host.RemoveManagedHosts());

            case IpcOperations.RunStrategyTests:
                return Ok(await host.RunStrategyTestsAsync(cancellationToken).ConfigureAwait(false));

            default:
                return IpcResponse.Failure(IpcErrorCode.UnknownOperation, $"Unknown operation '{request.Operation}'.");
        }
    }

    private static IpcResponse Ok<T>(T payload) => IpcResponse.Success(PipeProtocol.ToElement(payload));

    private static T Require<T>(IpcRequest request) =>
        PipeProtocol.FromElement<T>(request.Payload)
        ?? throw new InvalidOperationException($"Operation '{request.Operation}' requires a {typeof(T).Name} payload.");

    private static Task Respond(Stream pipe, IpcResponse response, CancellationToken cancellationToken) =>
        PipeProtocol.WriteMessageAsync(pipe, response, cancellationToken);

    private readonly record struct CallerIdentity(string Name, string Sid, bool IsAdministrator);

    /// <summary>
    /// Establishes who is calling by impersonating the client. Group membership is read from the
    /// caller's own token, so a standard user cannot claim to be an administrator.
    /// </summary>
    private CallerIdentity Identify(NamedPipeServerStream pipe)
    {
        var name = "unknown";
        var sid = "unknown";
        var isAdministrator = false;

        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                name = identity.Name;
                sid = identity.User?.Value ?? "unknown";
                isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not identify the pipe caller; treating it as unprivileged");
        }

        return new CallerIdentity(name, sid, isAdministrator);
    }
}
