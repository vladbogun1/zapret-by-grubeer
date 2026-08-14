using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zapret.Core.SystemIntegration;

/// <summary>
/// Upstream silently runs <c>netsh interface tcp set global timestamps=enabled</c> on every start
/// (<c>service.bat :tcp_enable</c>). The manager does the same, but records whether it was the one that
/// changed the setting so uninstall can offer to put it back — docs/flowseal-compatibility.md §5.7.
/// </summary>
public sealed class TcpTimestamps(ILogger<TcpTimestamps>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<TcpTimestamps>.Instance;

    /// <summary>
    /// Reads the current value through the CIM cmdlet rather than parsing <c>netsh</c> output, whose
    /// labels are localised. Null means "could not determine", in which case nothing is restored later.
    /// </summary>
    public async Task<bool?> TryReadEnabledAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            "powershell",
            ["-NoProfile", "-NonInteractive", "-Command", "(Get-NetTCPSetting -SettingName Internet).Timestamps"],
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0) return null;

        var value = result.Output.Trim();
        return value switch
        {
            var v when v.Equals("Enabled", StringComparison.OrdinalIgnoreCase) => true,
            var v when v.Equals("Disabled", StringComparison.OrdinalIgnoreCase) => false,
            _ => null,
        };
    }

    public Task<bool> EnableAsync(CancellationToken cancellationToken = default) => SetAsync("enabled", cancellationToken);

    public Task<bool> DisableAsync(CancellationToken cancellationToken = default) => SetAsync("disabled", cancellationToken);

    private async Task<bool> SetAsync(string value, CancellationToken cancellationToken)
    {
        // netsh, not the cmdlet: this is the exact command upstream relies on.
        var result = await RunAsync("netsh", ["interface", "tcp", "set", "global", $"timestamps={value}"], cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode == 0)
        {
            _logger.LogInformation("TCP timestamps set to {Value}", value);
            return true;
        }

        _logger.LogWarning("Could not set TCP timestamps to {Value}: {Output}", value, result.Output.Trim());
        return false;
    }

    private async Task<(int ExitCode, string Output)> RunAsync(string fileName, string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return (-1, string.Empty);

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return (process.ExitCode, output + error);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not run {FileName}", fileName);
            return (-1, ex.Message);
        }
    }
}

/// <summary>
/// Post-update reachability check. Informational by design: a failure never discards a healthy engine
/// build (docs/flowseal-compatibility.md §8.1).
/// </summary>
public sealed class HttpTargetProbe(HttpClient http, ILogger<HttpTargetProbe>? logger = null) : Engine.ITargetProbe
{
    private static readonly (string Name, string Url)[] Targets =
    [
        ("Discord", "https://discord.com/app"),
        ("YouTube", "https://www.youtube.com/generate_204"),
    ];

    private readonly ILogger _logger = logger ?? NullLogger<HttpTargetProbe>.Instance;

    public async Task<IReadOnlyDictionary<string, bool>> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, bool>(Targets.Length);

        foreach (var (name, url) in Targets)
        {
            results[name] = await ReachableAsync(url, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    private async Task<bool> ReachableAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            // Any answer at all means the connection was not torn down by DPI.
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Target {Url} is not reachable", url);
            return false;
        }
    }
}
