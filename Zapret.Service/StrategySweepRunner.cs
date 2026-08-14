using System.Diagnostics;
using System.Text;
using Zapret.Core.Flowseal;
using Zapret.Core.Model;
using Zapret.Core.Testing;

namespace Zapret.Service;

public sealed record SweepProgress(int Completed, int Total, string? CurrentStrategy);

/// <summary>
/// Drives upstream's <c>utils\test zapret.ps1</c> and reads its output.
/// <para>
/// The sweep is upstream's, not ours: their script already starts every discovered strategy in turn and
/// probes its own target list. Reimplementing that would mean maintaining a second, drifting definition of
/// what a meaningful test is. The manager's job is to run it unattended, follow progress, and turn the
/// output into ranked results (docs/flowseal-compatibility.md §5.6).
/// </para>
/// <para>
/// Two details make this work. The script is interactive — it asks for a checker mode and a run mode — so
/// stdin is redirected and answered with the defaults. And it starts engine processes itself, so the
/// manager's own engine must be stopped first: WinDivert allows only one capture at a time.
/// </para>
/// </summary>
public sealed class StrategySweepRunner(ILogger<StrategySweepRunner> logger)
{
    /// <summary>Checker mode 1 (standard), run mode 1 (all configs). Newlines answer both prompts.</summary>
    private const string ScriptAnswers = "1\n1\n";

    /// <summary>A full sweep of ~21 strategies against 17 targets takes minutes; this is the safety net.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(45);

    public sealed record SweepOutcome(bool Completed, IReadOnlyList<StrategyTestResult> Results, string? Error, string RawOutput);

    public async Task<SweepOutcome> RunAsync(
        EngineRuntimeInfo runtime,
        IProgress<SweepProgress>? progress,
        CancellationToken cancellationToken)
    {
        var script = UpstreamLayout.TestScript(runtime.Directory);
        if (!File.Exists(script))
        {
            return new SweepOutcome(false, Array.Empty<StrategyTestResult>(),
                $"{UpstreamLayout.TestScriptName} is not present in this engine build.", string.Empty);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            WorkingDirectory = runtime.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // The script prints box drawing and colour; UTF-8 keeps the parseable lines intact.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var output = new StringBuilder();
        var expectedTotal = runtime.SupportedStrategyCount;
        var completed = 0;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MaxDuration);

        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;

                lock (output) output.AppendLine(e.Data);

                // Progress comes from the script's own [n/total] headers rather than from a guess.
                var header = TryReadHeader(e.Data);
                if (header is null) return;

                completed = header.Value.Index;
                expectedTotal = header.Value.Total;
                progress?.Report(new SweepProgress(header.Value.Index - 1, header.Value.Total, header.Value.Name));
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) logger.LogWarning("test script: {Line}", e.Data);
            };

            if (!process.Start())
            {
                return new SweepOutcome(false, Array.Empty<StrategyTestResult>(), "The test utility could not be started.", string.Empty);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.StandardInput.WriteAsync(ScriptAnswers).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();

            logger.LogInformation("Strategy sweep started for engine {Version}", runtime.Version.Raw);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A cancelled or overrunning sweep still yields whatever completed, which is useful data.
                TryKill(process);

                var partial = ParseAndLog(output.ToString(), cancelled: true);
                return new SweepOutcome(false, partial,
                    cancellationToken.IsCancellationRequested ? null : $"The sweep exceeded {MaxDuration.TotalMinutes:0} minutes.",
                    output.ToString());
            }

            progress?.Report(new SweepProgress(expectedTotal, expectedTotal, null));

            var results = ParseAndLog(output.ToString(), cancelled: false);

            return results.Count == 0
                ? new SweepOutcome(false, results, "The test utility produced no readable results.", output.ToString())
                : new SweepOutcome(true, results, null, output.ToString());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogError(ex, "The strategy sweep failed to run");
            return new SweepOutcome(false, Array.Empty<StrategyTestResult>(), ex.Message, output.ToString());
        }
    }

    private IReadOnlyList<StrategyTestResult> ParseAndLog(string raw, bool cancelled)
    {
        var results = StrategyTestParser.Parse(raw);
        var best = StrategyTestParser.SelectBest(results);

        logger.LogInformation(
            "Strategy sweep {State}: {Count} strategies measured, best {Best}",
            cancelled ? "interrupted" : "finished",
            results.Count,
            best is null ? "(none passed)" : $"{best.StrategyId} at {best.SuccessPercent}%");

        return results;
    }

    private static (int Index, int Total, string Name)? TryReadHeader(string line)
    {
        var match = System.Text.RegularExpressions.Regex.Match(line, @"\[(\d+)\s*/\s*(\d+)\]\s*(.+?)(?:\.bat)?\s*$");
        if (!match.Success) return null;

        return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), match.Groups[3].Value.Trim());
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Could not stop the test utility");
        }
    }
}
