using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zapret.Core.Flowseal;
using Zapret.Core.Model;

namespace Zapret.Core.Engine;

/// <summary>
/// Managed-process run mode: the service owns <c>winws.exe</c> as a child process, supervises it, and
/// restarts it with backoff when it dies unexpectedly. This is the default because it gives accurate
/// status, a clean stop, and captured engine output — SPEC.md §7.
/// </summary>
public sealed class WinwsProcessController(ILogger<WinwsProcessController>? logger = null) : IEngineController, IDisposable
{
    private static readonly TimeSpan[] RestartBackoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(60),
    ];

    private readonly ILogger _logger = logger ?? NullLogger<WinwsProcessController>.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Process? _process;
    private EngineRuntimeInfo? _runtime;
    private StrategyDescriptor? _strategy;
    private bool _stopRequested;
    private int _consecutiveFailures;
    private CancellationTokenSource? _supervisor;

    public EngineState State { get; private set; } = EngineState.Stopped;

    /// <summary>Raised when the engine stops on its own, so the UI can show a native notification.</summary>
    public event Action<EngineState>? StateChanged;

    /// <summary>Raised when supervision gives up after repeated restart failures.</summary>
    public event Action<string>? SupervisionExhausted;

    public async Task<bool> StartAsync(EngineRuntimeInfo runtime, StrategyDescriptor strategy, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);

            if (!strategy.IsSupported)
            {
                return Fault($"strategy {strategy.DisplayName} is not usable with this engine build: {strategy.UnsupportedReason}");
            }

            var executable = UpstreamLayout.EngineExecutable(runtime.Directory);
            if (!File.Exists(executable))
            {
                return Fault($"{UpstreamLayout.EngineExecutableName} is missing from {runtime.Directory}");
            }

            WarnAboutForeignEngines();

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,

                // Upstream runs winws from its bin directory (cd /d %BIN%); cygwin builds care.
                WorkingDirectory = UpstreamLayout.Bin(runtime.Directory),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var argument in strategy.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            _runtime = runtime;
            _strategy = strategy;
            _stopRequested = false;

            try
            {
                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) => LogEngineOutput(e.Data, error: false);
                process.ErrorDataReceived += (_, e) => LogEngineOutput(e.Data, error: true);
                process.Exited += OnProcessExited;

                if (!process.Start())
                {
                    return Fault($"{UpstreamLayout.EngineExecutableName} could not be started");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                _process = process;
                _supervisor = new CancellationTokenSource();
                _consecutiveFailures = 0;

                Transition(new EngineState(EngineStatus.Running, strategy.Id, runtime.Version.Raw, DateTimeOffset.UtcNow));
                _logger.LogInformation(
                    "Engine started: strategy {Strategy}, engine {Version}, pid {Pid}, {Count} arguments",
                    strategy.Id, runtime.Version.Raw, process.Id, strategy.Arguments.Count);

                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // The usual cause is the WinDivert driver being blocked by antivirus.
                return Fault($"{UpstreamLayout.EngineExecutableName} could not be started: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsHealthyAsync(TimeSpan settle, CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (process is null || process.HasExited) return false;

        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(settle, cancellationToken).ConfigureAwait(false);

            // Exiting inside the settle window is exactly what "unhealthy" means.
            return false;
        }
        catch (TimeoutException)
        {
            return !process.HasExited;
        }
        catch (OperationCanceledException)
        {
            return !process.HasExited;
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        _stopRequested = true;
        _supervisor?.Cancel();
        _supervisor?.Dispose();
        _supervisor = null;

        var process = _process;
        _process = null;

        if (process is null)
        {
            Transition(EngineState.Stopped);
            return;
        }

        try
        {
            process.Exited -= OnProcessExited;

            if (!process.HasExited)
            {
                // winws has no graceful shutdown channel; upstream stops it the same way.
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Engine stopped");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "The engine process did not stop cleanly");
        }
        finally
        {
            process.Dispose();
            Transition(EngineState.Stopped);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (_stopRequested) return;

        var exitCode = (sender as Process)?.ExitCode;
        _logger.LogWarning("The engine exited unexpectedly with code {ExitCode}", exitCode);

        Transition(State with
        {
            Status = EngineStatus.Faulted,
            LastError = $"The engine stopped unexpectedly (exit code {exitCode}).",
        });

        _ = SuperviseAsync();
    }

    /// <summary>Restarts an unexpectedly stopped engine, backing off and eventually giving up loudly.</summary>
    private async Task SuperviseAsync()
    {
        var runtime = _runtime;
        var strategy = _strategy;
        if (runtime is null || strategy is null) return;

        var attempt = Math.Min(_consecutiveFailures, RestartBackoff.Length - 1);
        var delay = RestartBackoff[attempt];
        _consecutiveFailures++;

        if (_consecutiveFailures > RestartBackoff.Length)
        {
            var message = $"The engine stopped {_consecutiveFailures - 1} times in a row and will not be restarted automatically.";
            _logger.LogError("{Message}", message);
            SupervisionExhausted?.Invoke(message);
            return;
        }

        _logger.LogInformation("Restarting the engine in {Delay}s (attempt {Attempt})", delay.TotalSeconds, _consecutiveFailures);

        try
        {
            await Task.Delay(delay).ConfigureAwait(false);
            await StartAsync(runtime, strategy).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Automatic engine restart failed");
        }
    }

    /// <summary>
    /// An engine started outside the manager (upstream's own .bat) would fight ours over WinDivert.
    /// Worth a log line, not worth killing someone else's process behind their back.
    /// </summary>
    private void WarnAboutForeignEngines()
    {
        try
        {
            var others = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(UpstreamLayout.EngineExecutableName));
            if (others.Length == 0) return;

            _logger.LogWarning(
                "{Count} winws.exe process(es) are already running, possibly started outside {Product}. WinDivert allows only one.",
                others.Length, AppPaths.DisplayName);

            foreach (var other in others) other.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Could not enumerate existing engine processes");
        }
    }

    private void LogEngineOutput(string? line, bool error)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        if (error) _logger.LogWarning("winws: {Line}", line);
        else _logger.LogInformation("winws: {Line}", line);
    }

    private bool Fault(string message)
    {
        _logger.LogError("{Message}", message);
        Transition(EngineState.Stopped with { Status = EngineStatus.Faulted, LastError = message });
        return false;
    }

    private void Transition(EngineState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        _supervisor?.Cancel();
        _supervisor?.Dispose();
        _process?.Dispose();
        _gate.Dispose();
    }
}
