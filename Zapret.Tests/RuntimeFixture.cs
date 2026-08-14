using Zapret.Core.Flowseal;
using Zapret.Core.Model;

namespace Zapret.Tests;

/// <summary>
/// Builds a throwaway engine runtime out of the real upstream <c>.bat</c> fixtures, so adapter tests
/// exercise discovery, capability detection and reference validation against genuine strategy files.
/// </summary>
public sealed class RuntimeFixture : IDisposable
{
    public string Root { get; }

    public static string FixtureDirectory(string version = "1.10.1") =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "upstream", version);

    private RuntimeFixture(string root) => Root = root;

    /// <summary>
    /// Copies the fixture strategies into a temp directory, then materialises every file those
    /// strategies reference plus the components a real build ships. Content is irrelevant: the
    /// manager only ever checks existence.
    /// </summary>
    public static RuntimeFixture CreateComplete(string version = "1.10.1", bool includeServiceBat = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "zapret-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        foreach (var file in Directory.EnumerateFiles(FixtureDirectory(version), "*.bat"))
        {
            var name = Path.GetFileName(file);
            if (!includeServiceBat && name.StartsWith("service", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(root, name));
        }

        foreach (var directory in new[]
                 {
                     UpstreamLayout.Bin(root), UpstreamLayout.Lists(root),
                     UpstreamLayout.Utils(root), UpstreamLayout.ServiceDirectory(root),
                 })
        {
            Directory.CreateDirectory(directory);
        }

        Touch(UpstreamLayout.EngineExecutable(root));
        Touch(Path.Combine(UpstreamLayout.Bin(root), UpstreamLayout.WinDivertLibraryName));
        Touch(Path.Combine(UpstreamLayout.Bin(root), UpstreamLayout.WinDivertDriverName));
        Touch(Path.Combine(UpstreamLayout.Bin(root), UpstreamLayout.CygwinRuntimeName));
        Touch(Path.Combine(UpstreamLayout.Bin(root), UpstreamLayout.ActiveDiscordFakeName));
        Touch(Path.Combine(UpstreamLayout.Bin(root), UpstreamLayout.ActiveGameFakeName));
        Touch(UpstreamLayout.TestScript(root));
        Touch(Path.Combine(UpstreamLayout.Utils(root), UpstreamLayout.TestTargetsName));
        Touch(UpstreamLayout.HostsPayload(root));
        Touch(UpstreamLayout.IpSetPayload(root));
        Touch(UpstreamLayout.IpSetAll(root));
        Touch(UpstreamLayout.IpSetAllBackup(root));
        File.WriteAllText(UpstreamLayout.VersionFile(root), version + "\n");

        // Whatever the strategies point at, in every game filter mode, must exist.
        foreach (var mode in Enum.GetValues<GameFilterMode>())
        {
            var context = new StrategyParseContext(root, new GameFilterState(mode));
            foreach (var file in Directory.EnumerateFiles(root, "*.bat"))
            {
                if (!UpstreamLayout.IsStrategyFile(Path.GetFileName(file))) continue;

                var strategy = StrategyBatParser.Parse(file, context);
                foreach (var path in strategy.ReferencedPaths)
                {
                    Touch(path);
                }
            }
        }

        return new RuntimeFixture(root);
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllBytes(path, Array.Empty<byte>());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory must never fail a test run.
        }
    }
}
