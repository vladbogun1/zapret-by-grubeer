namespace Zapret.Core;

/// <summary>
/// Every path the product uses. Nothing here assumes drive C:, and nothing mutable ever
/// lands next to the executable — see SPEC.md §5.
/// </summary>
public static class AppPaths
{
    /// <summary>ASCII name used for anything a filesystem or the SCM touches.</summary>
    public const string AsciiName = "ZapretByGrubeer";

    /// <summary>The one and only display name, used in UI, logs and installer metadata.</summary>
    public const string DisplayName = "Запрет by Grubeer";

    public const string PipeName = "ZapretByGrubeer";
    public const string ManagerServiceName = "ZapretByGrubeer";

    /// <summary>Upstream's own service name. Observed constant, see flowseal-compatibility.md §2.1.</summary>
    public const string UpstreamServiceName = "zapret";

    public const string UpstreamRegistryKey = @"SYSTEM\CurrentControlSet\Services\zapret";
    public const string UpstreamRegistryValue = "zapret-discord-youtube";

    /// <summary>Directory the running binaries live in. Resolved, never assumed.</summary>
    public static string InstallDirectory => AppContext.BaseDirectory;

    public static string ProgramData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AsciiName);

    public static string LocalAppData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AsciiName);

    public static string RuntimeRoot { get; } = Path.Combine(ProgramData, "runtime");
    public static string RuntimeVersions { get; } = Path.Combine(RuntimeRoot, "versions");
    public static string RuntimeStaging { get; } = Path.Combine(RuntimeRoot, "staging");
    public static string CurrentStateFile { get; } = Path.Combine(RuntimeRoot, "current.json");

    public static string Data { get; } = Path.Combine(ProgramData, "data");
    public static string DataLists { get; } = Path.Combine(Data, "lists");
    public static string HostsBackups { get; } = Path.Combine(Data, "backups", "hosts");
    public static string SettingsFile { get; } = Path.Combine(Data, "settings.json");
    public static string EngineStateFile { get; } = Path.Combine(Data, "engine.json");

    public static string Logs { get; } = Path.Combine(ProgramData, "logs");

    public static string UserInterfaceStateFile { get; } = Path.Combine(LocalAppData, "ui.json");
    public static string GitHubCache { get; } = Path.Combine(LocalAppData, "cache", "github");

    public static string SystemHostsFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public static string VersionDirectory(string version) => Path.Combine(RuntimeVersions, version);

    public static void EnsureMachineDirectories()
    {
        foreach (var path in new[] { ProgramData, RuntimeRoot, RuntimeVersions, RuntimeStaging, Data, DataLists, HostsBackups, Logs })
        {
            Directory.CreateDirectory(path);
        }
    }

    public static void EnsureUserDirectories()
    {
        Directory.CreateDirectory(LocalAppData);
        Directory.CreateDirectory(GitHubCache);
    }
}

/// <summary>
/// Upstream warns against paths containing Cyrillic or special characters, and
/// <c>winws.exe</c> is a cygwin build, so the engine runtime is kept deliberately boring.
/// The manager's own install directory is not subject to this — it never hosts the engine.
/// </summary>
public static class EnginePathGuard
{
    public static bool IsSafeForEngine(string path, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "path is empty";
            return false;
        }

        foreach (var c in path)
        {
            if (c > 0x7E)
            {
                reason = $"path contains the non-ASCII character '{c}', which upstream scripts and the cygwin-based winws.exe do not handle reliably";
                return false;
            }
        }

        var invalid = path.IndexOfAny(new[] { '%', '^', '&', '(', ')', '!', ',', ';', '=' });
        if (invalid >= 0)
        {
            reason = $"path contains the character '{path[invalid]}', which breaks batch and service command lines";
            return false;
        }

        reason = null;
        return true;
    }
}
