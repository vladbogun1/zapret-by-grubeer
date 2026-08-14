using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zapret.Core.Model;

namespace Zapret.Core.Flowseal;

/// <summary>
/// Generic adapter: everything is discovered, nothing is assumed. This implementation is expected
/// to keep working across upstream releases; a version-specific subclass is only justified by a
/// documented breaking change (docs/flowseal-compatibility.md §9.1).
/// </summary>
public class FlowsealAdapter(ILogger<FlowsealAdapter>? logger = null) : IFlowsealAdapter
{
    private readonly ILogger _logger = logger ?? NullLogger<FlowsealAdapter>.Instance;

    public EngineRuntimeInfo Inspect(string runtimeDirectory, string? releaseTag = null)
    {
        var checks = new List<CompatibilityCheck>();

        var (version, allVersions) = EngineVersionDetector.Detect(runtimeDirectory, releaseTag);
        if (EngineVersionDetector.HasConflict(allVersions))
        {
            var detail = string.Join(", ", allVersions.Select(v => $"{v.Raw} ({v.Source})"));
            _logger.LogWarning("Engine version sources disagree: {Sources}", detail);
            checks.Add(new CompatibilityCheck("engine version", true,
                $"sources disagree: {detail}; using {version.Raw}", CompatibilitySeverity.Information));
        }
        else
        {
            checks.Add(new CompatibilityCheck("engine version", version.IsKnown,
                version.IsKnown ? $"{version.Raw} (from {version.Source})" : "no version file or LOCAL_VERSION constant found",
                CompatibilitySeverity.Limitation));
        }

        // Required components. Their absence means this is not a usable Zapret build.
        var enginePath = UpstreamLayout.EngineExecutable(runtimeDirectory);
        checks.Add(new CompatibilityCheck("winws", File.Exists(enginePath),
            File.Exists(enginePath) ? enginePath : $"{UpstreamLayout.BinDirectoryName}\\{UpstreamLayout.EngineExecutableName} is missing",
            CompatibilitySeverity.Blocker));

        var bin = UpstreamLayout.Bin(runtimeDirectory);
        var driverPresent = File.Exists(Path.Combine(bin, UpstreamLayout.WinDivertLibraryName))
                            && File.Exists(Path.Combine(bin, UpstreamLayout.WinDivertDriverName));
        checks.Add(new CompatibilityCheck("WinDivert driver", driverPresent,
            driverPresent ? "present" : $"{UpstreamLayout.WinDivertLibraryName} or {UpstreamLayout.WinDivertDriverName} is missing",
            CompatibilitySeverity.Blocker));

        var cygwinPresent = File.Exists(Path.Combine(bin, UpstreamLayout.CygwinRuntimeName));
        checks.Add(new CompatibilityCheck("cygwin runtime", cygwinPresent,
            cygwinPresent ? "present" : $"{UpstreamLayout.CygwinRuntimeName} is missing; upstream may have changed toolchain",
            CompatibilitySeverity.Information));

        // User lists first: upstream strategies reference them unconditionally.
        EnsureUserLists(runtimeDirectory);

        var gameFilter = ReadGameFilter(runtimeDirectory);
        var strategies = DiscoverStrategies(runtimeDirectory, gameFilter);
        var supported = strategies.Count(s => s.IsSupported);

        checks.Add(new CompatibilityCheck("strategies", supported > 0,
            supported > 0
                ? $"{supported} of {strategies.Count} discovered strategies are usable"
                : "no usable strategy could be parsed from this build",
            CompatibilitySeverity.Blocker));

        if (supported > 0 && supported < strategies.Count)
        {
            var broken = strategies.Where(s => !s.IsSupported).Select(s => $"{s.Id}: {s.UnsupportedReason}");
            checks.Add(new CompatibilityCheck("strategy parsing", false,
                "some strategies are unavailable — " + string.Join("; ", broken),
                CompatibilitySeverity.Limitation));
        }

        var capabilities = CapabilityDetector.Detect(runtimeDirectory);
        AddCapabilityChecks(checks, capabilities);

        var report = BuildReport(checks);

        _logger.LogInformation(
            "Inspected engine at {Directory}: version {Version}, {Supported}/{Total} strategies, outcome {Outcome}",
            runtimeDirectory, version.Raw, supported, strategies.Count, report.Outcome);

        return new EngineRuntimeInfo
        {
            Directory = runtimeDirectory,
            Version = version,
            Capabilities = capabilities,
            Strategies = strategies,
            Report = report,
            GameFilter = gameFilter,
            IpSet = ReadIpSet(runtimeDirectory),
        };
    }

    public IReadOnlyList<StrategyDescriptor> DiscoverStrategies(string runtimeDirectory, GameFilterState gameFilter)
    {
        if (!Directory.Exists(runtimeDirectory)) return Array.Empty<StrategyDescriptor>();

        var context = new StrategyParseContext(runtimeDirectory, gameFilter);

        var files = Directory
            .EnumerateFiles(runtimeDirectory, "*.bat", SearchOption.TopDirectoryOnly)
            .Where(path => UpstreamLayout.IsStrategyFile(Path.GetFileName(path)))
            .OrderBy(path => Path.GetFileName(path)!, NaturalNameComparer.Instance)
            .ToList();

        var result = new List<StrategyDescriptor>(files.Count);

        foreach (var file in files)
        {
            var descriptor = StrategyBatParser.Parse(file, context);
            result.Add(descriptor.IsSupported ? ValidateReferences(descriptor) : descriptor);

            if (!result[^1].IsSupported)
            {
                _logger.LogWarning("Strategy {Id} is unavailable: {Reason}", result[^1].Id, result[^1].UnsupportedReason);
            }
        }

        return result;
    }

    /// <summary>
    /// A strategy that points at a file the build does not contain cannot run. Missing user lists are
    /// only a warning: they are manager-owned and created on demand.
    /// </summary>
    private static StrategyDescriptor ValidateReferences(StrategyDescriptor descriptor)
    {
        var missing = new List<string>();
        var warnings = new List<string>(descriptor.Warnings);

        foreach (var path in descriptor.ReferencedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path)) continue;

            if (UpstreamLayout.IsUserList(Path.GetFileName(path)))
            {
                warnings.Add($"user list {Path.GetFileName(path)} is missing and will be created");
                continue;
            }

            missing.Add(path);
        }

        if (missing.Count > 0)
        {
            return descriptor with
            {
                Warnings = warnings,
                UnsupportedReason = "referenced file(s) not present in this build: " + string.Join(", ", missing.Select(Path.GetFileName)),
            };
        }

        return descriptor with { Warnings = warnings };
    }

    public GameFilterState ReadGameFilter(string runtimeDirectory)
    {
        var flag = UpstreamLayout.GameFilterFlag(runtimeDirectory);
        if (!File.Exists(flag)) return GameFilterState.Off;

        try
        {
            using var reader = new StreamReader(flag);
            return GameFilterState.FromFlagFile(reader.ReadLine() ?? string.Empty);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read the game filter flag at {Path}; treating it as disabled", flag);
            return GameFilterState.Off;
        }
    }

    public void WriteGameFilter(string runtimeDirectory, GameFilterState state)
    {
        var flag = UpstreamLayout.GameFilterFlag(runtimeDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(flag)!);

        var content = state.FlagFileContent;
        if (content is null)
        {
            if (File.Exists(flag)) File.Delete(flag);
            return;
        }

        // Upstream writes with `echo tcp>file`, producing the value plus CRLF.
        File.WriteAllText(flag, content + "\r\n");
    }

    public IpSetMode ReadIpSet(string runtimeDirectory)
    {
        var path = UpstreamLayout.IpSetAll(runtimeDirectory);
        if (!File.Exists(path)) return IpSetMode.Any;

        try
        {
            return IpSetState.Detect(File.ReadAllText(path));
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read {Path}; reporting IPSet as any", path);
            return IpSetMode.Any;
        }
    }

    public void EnsureUserLists(string runtimeDirectory)
    {
        var lists = UpstreamLayout.Lists(runtimeDirectory);
        if (!Directory.Exists(lists)) return;

        foreach (var (name, placeholder) in UpstreamLayout.UserLists)
        {
            var path = Path.Combine(lists, name);
            if (File.Exists(path) && new FileInfo(path).Length > 0) continue;

            try
            {
                File.WriteAllText(path, placeholder);
                _logger.LogInformation("Created user list {Name} with upstream placeholder content", name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not create user list {Path}", path);
            }
        }
    }

    public string ResolveEngineExecutable(string runtimeDirectory) => UpstreamLayout.EngineExecutable(runtimeDirectory);

    private static void AddCapabilityChecks(List<CompatibilityCheck> checks, UpstreamCapabilities capabilities)
    {
        void Add(string name, bool value, string missingDetail) =>
            checks.Add(new CompatibilityCheck(name, value, value ? "detected" : missingDetail, CompatibilitySeverity.Limitation));

        Add("service management", capabilities.SupportsUpstreamServiceMode, "engine executable missing, upstream service mode unavailable");
        Add("user lists", capabilities.SupportsUserDomainLists, $"{UpstreamLayout.ListsDirectoryName} directory missing");
        Add("test utility", capabilities.SupportsStrategyTests, $"{UpstreamLayout.UtilsDirectoryName}\\{UpstreamLayout.TestScriptName} missing");
        Add("game filter", capabilities.SupportsGameFilter, $"{UpstreamLayout.UtilsDirectoryName} directory missing");
        Add("IPSet filter", capabilities.SupportsIpSetFilter, $"{UpstreamLayout.IpSetAllName} missing");
        Add("IPSet update", capabilities.SupportsIpSetUpdate, $"{UpstreamLayout.ListsDirectoryName} directory missing");
        // Not a limitation of the build: release archives never ship .service, so the hosts payload is
        // fetched from upstream's repository at use time.
        Add("hosts entries", capabilities.SupportsHostsUpdater, "unavailable");
        Add("fake replacement", capabilities.SupportsFakeReplacement, "no replaceable fake pairs found in bin");
    }

    private static CompatibilityReport BuildReport(List<CompatibilityCheck> checks)
    {
        if (checks.Any(c => !c.Passed && c.Severity == CompatibilitySeverity.Blocker))
        {
            return new CompatibilityReport(CompatibilityOutcome.Incompatible, checks);
        }

        return checks.Any(c => !c.Passed && c.Severity == CompatibilitySeverity.Limitation)
            ? new CompatibilityReport(CompatibilityOutcome.CompatibleWithLimitations, checks)
            : new CompatibilityReport(CompatibilityOutcome.Compatible, checks);
    }
}
