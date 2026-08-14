namespace Zapret.Core.Flowseal;

/// <summary>
/// The only place that knows upstream's file names. Everything else asks this class, so a future
/// upstream rename is a single edit plus a compatibility-report entry.
/// See docs/flowseal-compatibility.md §2.
/// </summary>
public static class UpstreamLayout
{
    public const string BinDirectoryName = "bin";
    public const string ListsDirectoryName = "lists";
    public const string UtilsDirectoryName = "utils";
    public const string ServiceDirectoryName = ".service";

    public const string EngineExecutableName = "winws.exe";
    public const string WinDivertLibraryName = "WinDivert.dll";
    public const string WinDivertDriverName = "WinDivert64.sys";
    public const string CygwinRuntimeName = "cygwin1.dll";

    public const string ServiceBatName = "service.bat";
    public const string StrategyExclusionPrefix = "service";

    public const string GameFilterFlagName = "game_filter.enabled";
    public const string UpdateCheckFlagName = "check_updates.enabled";
    public const string TestScriptName = "test zapret.ps1";
    public const string TestTargetsName = "targets.txt";

    public const string VersionFileName = "version.txt";
    public const string HostsPayloadName = "hosts";
    public const string IpSetPayloadName = "ipset-service.txt";

    public const string IpSetAllName = "ipset-all.txt";
    public const string IpSetAllBackupName = "ipset-all.txt.backup";

    public const string ActiveDiscordFakeName = "ACTIVE_DISCORD_UDP.bin";
    public const string ActiveGameFakeName = "ACTIVE_GAME_UDP.bin";
    public const string ActiveFakePrefix = "ACTIVE_";

    public static string Bin(string root) => Path.Combine(root, BinDirectoryName);
    public static string Lists(string root) => Path.Combine(root, ListsDirectoryName);
    public static string Utils(string root) => Path.Combine(root, UtilsDirectoryName);
    public static string ServiceDirectory(string root) => Path.Combine(root, ServiceDirectoryName);

    public static string EngineExecutable(string root) => Path.Combine(Bin(root), EngineExecutableName);
    public static string ServiceBat(string root) => Path.Combine(root, ServiceBatName);
    public static string GameFilterFlag(string root) => Path.Combine(Utils(root), GameFilterFlagName);
    public static string UpdateCheckFlag(string root) => Path.Combine(Utils(root), UpdateCheckFlagName);
    public static string TestScript(string root) => Path.Combine(Utils(root), TestScriptName);
    public static string VersionFile(string root) => Path.Combine(ServiceDirectory(root), VersionFileName);
    public static string HostsPayload(string root) => Path.Combine(ServiceDirectory(root), HostsPayloadName);
    public static string IpSetPayload(string root) => Path.Combine(ServiceDirectory(root), IpSetPayloadName);
    public static string IpSetAll(string root) => Path.Combine(Lists(root), IpSetAllName);
    public static string IpSetAllBackup(string root) => Path.Combine(Lists(root), IpSetAllBackupName);

    /// <summary>
    /// User-owned list files. Upstream's strategies reference them unconditionally, and upstream
    /// creates them on demand with this exact placeholder content — an empty or missing file breaks
    /// <c>winws</c>. See docs/flowseal-compatibility.md §5.4.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> UserLists = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["list-general-user.txt"] = "# Never leave this file empty\r\ndomain.example.abc\r\n",
        ["list-exclude-user.txt"] = "domain.example.abc\r\n",
        ["ipset-exclude-user.txt"] = "203.0.113.113/32\r\n",
    };

    public static bool IsUserList(string fileName) => UserLists.ContainsKey(fileName);

    /// <summary>Upstream's own rule: every root .bat that does not start with "service".</summary>
    public static bool IsStrategyFile(string fileName) =>
        fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
        && !fileName.StartsWith(StrategyExclusionPrefix, StringComparison.OrdinalIgnoreCase);
}
