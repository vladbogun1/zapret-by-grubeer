using System.Text.RegularExpressions;
using Zapret.Core.Model;

namespace Zapret.Core.Flowseal;

/// <summary>
/// Engine version detection from three independent sources, in the priority order defined by
/// docs/flowseal-compatibility.md §3. A build with no detectable version is still usable.
/// </summary>
public static class EngineVersionDetector
{
    private static readonly Regex LocalVersion = new(
        @"^\s*@?set\s+""?LOCAL_VERSION=(?<version>[^""\r\n]+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public static IReadOnlyList<EngineVersion> DetectAll(string runtimeDirectory, string? releaseTag = null)
    {
        var found = new List<EngineVersion>();

        var versionFile = UpstreamLayout.VersionFile(runtimeDirectory);
        if (File.Exists(versionFile))
        {
            var value = SafeReadFirstLine(versionFile);
            if (!string.IsNullOrWhiteSpace(value))
            {
                found.Add(new EngineVersion(value.Trim(), EngineVersionSource.ServiceVersionFile));
            }
        }

        var serviceBat = UpstreamLayout.ServiceBat(runtimeDirectory);
        if (File.Exists(serviceBat))
        {
            var text = SafeReadAllText(serviceBat);
            var match = LocalVersion.Match(text);
            if (match.Success)
            {
                found.Add(new EngineVersion(match.Groups["version"].Value.Trim(), EngineVersionSource.ServiceBatConstant));
            }
        }

        if (!string.IsNullOrWhiteSpace(releaseTag))
        {
            found.Add(new EngineVersion(EngineVersion.NormalizeTag(releaseTag), EngineVersionSource.ReleaseTag));
        }

        return found;
    }

    /// <summary>Primary version for display, plus every source found, so disagreement can be logged.</summary>
    public static (EngineVersion Primary, IReadOnlyList<EngineVersion> All) Detect(string runtimeDirectory, string? releaseTag = null)
    {
        var all = DetectAll(runtimeDirectory, releaseTag);
        var primary = all.Count > 0 ? all[0] : EngineVersion.Unknown;
        return (primary, all);
    }

    /// <summary>True when two sources disagree, which is a warning and never an error.</summary>
    public static bool HasConflict(IReadOnlyList<EngineVersion> versions) =>
        versions.Select(v => EngineVersion.NormalizeTag(v.Raw))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() > 1;

    private static string SafeReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static string? SafeReadFirstLine(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            return reader.ReadLine();
        }
        catch (IOException)
        {
            return null;
        }
    }
}
