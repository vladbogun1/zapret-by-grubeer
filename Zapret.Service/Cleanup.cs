using Zapret.Core;
using Zapret.Core.Engine;
using Zapret.Core.SystemIntegration;

namespace Zapret.Service;

/// <summary>
/// Uninstall-time cleanup, run as <c>ZapretByGrubeer.Service.exe --cleanup</c> by the uninstaller.
/// Removes exactly what the manager created and nothing else: an unrelated hosts entry, an unrelated
/// service, or a TCP setting the manager never touched is left alone (SPEC.md §10.1).
/// </summary>
internal static class Cleanup
{
    public static async Task<int> RunAsync(bool removeEngine, bool keepSettings)
    {
        var log = new List<string>();

        void Report(string line)
        {
            log.Add(line);
            Console.WriteLine(line);
        }

        Report($"{AppPaths.DisplayName}: cleanup starting (removeEngine={removeEngine}, keepSettings={keepSettings})");

        // 1. The engine service and the WinDivert driver services upstream also removes.
        try
        {
            var upstream = new UpstreamServiceEngineController();
            await upstream.RemoveServiceAsync().ConfigureAwait(false);
            Report("Removed the engine service and WinDivert driver registrations.");
        }
        catch (Exception ex)
        {
            Report("Could not remove the engine service: " + ex.Message);
        }

        // 2. Any winws.exe left running from a managed-process session.
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("winws"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                Report($"Stopped a running engine process (pid {process.Id}).");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Report("Could not stop a running engine process: " + ex.Message);
            }
            finally
            {
                process.Dispose();
            }
        }

        // 3. The managed hosts section, and only that section.
        try
        {
            if (new HostsManager().Remove()) Report("Removed the managed hosts section.");
        }
        catch (Exception ex)
        {
            Report("Could not clean the hosts file: " + ex.Message);
        }

        var settings = new SettingsStore().Read();

        // 4. TCP timestamps, but only if the manager was the one that enabled them.
        if (settings.TcpTimestampsEnabledByManager && settings.TcpTimestampsValueBeforeManager == false)
        {
            try
            {
                if (await new TcpTimestamps().DisableAsync().ConfigureAwait(false))
                {
                    Report("Restored the TCP timestamps setting the manager had changed.");
                }
            }
            catch (Exception ex)
            {
                Report("Could not restore TCP timestamps: " + ex.Message);
            }
        }

        // 5. The engine runtime, only when asked.
        if (removeEngine) Delete(AppPaths.RuntimeRoot, "engine runtime", Report);

        // 6. Settings, lists and logs, unless the user chose to keep them.
        if (!keepSettings)
        {
            Delete(AppPaths.Data, "settings and lists", Report);
            Delete(AppPaths.Logs, "logs", Report);
        }
        else
        {
            Report("Kept settings, custom lists and logs.");
        }

        // 7. The ProgramData root, if nothing is left in it.
        try
        {
            if (Directory.Exists(AppPaths.ProgramData) &&
                !Directory.EnumerateFileSystemEntries(AppPaths.ProgramData).Any())
            {
                Directory.Delete(AppPaths.ProgramData);
                Report("Removed the empty application data folder.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report("Could not remove the application data folder: " + ex.Message);
        }

        Report("Cleanup finished. Unrelated network configuration was not modified.");
        return 0;
    }

    private static void Delete(string path, string what, Action<string> report)
    {
        try
        {
            if (!Directory.Exists(path)) return;

            Directory.Delete(path, recursive: true);
            report($"Removed {what}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report($"Could not remove {what}: {ex.Message}");
        }
    }
}
