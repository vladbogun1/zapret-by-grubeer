using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Zapret.Core.Flowseal;
using Zapret.Core.Model;

namespace Zapret.Core.Engine;

/// <summary>
/// Upstream-compatible run mode: the engine runs as the Windows service named <c>zapret</c>, created
/// the same way <c>service.bat</c> creates it, including the registry marker upstream uses to remember
/// the selected strategy. That keeps a manager-installed engine legible to upstream tooling and lets
/// the manager adopt an engine installed by upstream — SPEC.md §7.
/// </summary>
public sealed class UpstreamServiceEngineController(ILogger<UpstreamServiceEngineController>? logger = null) : IEngineController
{
    private const string DisplayName = "zapret";
    private const string Description = "Zapret DPI bypass software";

    private readonly ILogger _logger = logger ?? NullLogger<UpstreamServiceEngineController>.Instance;

    public EngineState State { get; private set; } = EngineState.Stopped;

    public async Task<bool> StartAsync(EngineRuntimeInfo runtime, StrategyDescriptor strategy, CancellationToken cancellationToken = default)
    {
        if (!strategy.IsSupported)
        {
            return Fault($"strategy {strategy.DisplayName} is not usable with this engine build: {strategy.UnsupportedReason}");
        }

        var executable = UpstreamLayout.EngineExecutable(runtime.Directory);
        if (!File.Exists(executable))
        {
            return Fault($"{UpstreamLayout.EngineExecutableName} is missing from {runtime.Directory}");
        }

        // Upstream recreates the service on every apply, because the arguments are baked into binPath.
        await RemoveServiceAsync(cancellationToken).ConfigureAwait(false);

        var binPath = BuildBinPath(executable, strategy.Arguments);

        var created = await ScAsync(cancellationToken,
            "create", AppPaths.UpstreamServiceName,
            "binPath=", binPath,
            "DisplayName=", DisplayName,
            "start=", "auto");

        if (created.ExitCode != 0)
        {
            return Fault($"the {AppPaths.UpstreamServiceName} service could not be created: {created.Output.Trim()}");
        }

        await ScAsync(cancellationToken, "description", AppPaths.UpstreamServiceName, Description).ConfigureAwait(false);
        WriteStrategyMarker(strategy.Id);

        var started = await ScAsync(cancellationToken, "start", AppPaths.UpstreamServiceName).ConfigureAwait(false);
        if (started.ExitCode != 0)
        {
            return Fault($"the {AppPaths.UpstreamServiceName} service could not be started: {started.Output.Trim()}");
        }

        State = new EngineState(EngineStatus.Running, strategy.Id, runtime.Version.Raw, DateTimeOffset.UtcNow);
        _logger.LogInformation("Engine running as the {Service} service with strategy {Strategy}", AppPaths.UpstreamServiceName, strategy.Id);
        return true;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await ScAsync(cancellationToken, "stop", AppPaths.UpstreamServiceName).ConfigureAwait(false);
        State = EngineState.Stopped;
    }

    public async Task<bool> IsHealthyAsync(TimeSpan settle, CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Delay(settle, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation only shortens the observation window.
        }

        return QueryStatus() == ServiceControllerStatus.Running;
    }

    /// <summary>Removes the engine service, and the WinDivert driver services upstream also cleans up.</summary>
    public async Task RemoveServiceAsync(CancellationToken cancellationToken = default)
    {
        await ScAsync(cancellationToken, "stop", AppPaths.UpstreamServiceName).ConfigureAwait(false);
        await ScAsync(cancellationToken, "delete", AppPaths.UpstreamServiceName).ConfigureAwait(false);

        foreach (var driver in new[] { "WinDivert", "WinDivert14" })
        {
            await ScAsync(cancellationToken, "stop", driver).ConfigureAwait(false);
            await ScAsync(cancellationToken, "delete", driver).ConfigureAwait(false);
        }

        State = EngineState.Stopped;
    }

    /// <summary>True when an engine service exists, whoever created it.</summary>
    public bool IsServiceInstalled() => QueryStatus() is not null;

    public ServiceControllerStatus? QueryStatus()
    {
        try
        {
            using var controller = new ServiceController(AppPaths.UpstreamServiceName);
            return controller.Status;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The strategy upstream (or the manager) last installed, read from upstream's own marker.</summary>
    public string? ReadStrategyMarker()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AppPaths.UpstreamRegistryKey);
            return key?.GetValue(AppPaths.UpstreamRegistryValue) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read the upstream strategy marker");
            return null;
        }
    }

    private void WriteStrategyMarker(string strategyId)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(AppPaths.UpstreamRegistryKey, writable: true);
            key?.SetValue(AppPaths.UpstreamRegistryValue, strategyId, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write the upstream strategy marker");
        }
    }

    /// <summary>
    /// One string for <c>binPath=</c>: the executable and every argument, quoted only where needed.
    /// The service control manager re-parses this, so quoting has to be right here rather than left to
    /// the batch gymnastics upstream performs.
    /// </summary>
    internal static string BuildBinPath(string executable, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { Quote(executable) }.Concat(arguments.Select(Quote)));

    private static string Quote(string value) =>
        value.Length > 0 && !value.Any(c => c is ' ' or '\t' or '"') ? value : '"' + value.Replace("\"", "\\\"") + '"';

    private async Task<(int ExitCode, string Output)> ScAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "sc",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // ArgumentList quotes each element properly, which is what upstream's batch cannot do reliably.
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return (-1, "sc.exe could not be started");

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return (process.ExitCode, output + error);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            return (-1, ex.Message);
        }
    }

    private bool Fault(string message)
    {
        _logger.LogError("{Message}", message);
        State = EngineState.Stopped with { Status = EngineStatus.Faulted, LastError = message };
        return false;
    }
}
