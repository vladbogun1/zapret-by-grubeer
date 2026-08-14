using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zapret.Core.Engine;
using Zapret.Core.GitHub;

namespace Zapret.Core;

/// <summary>
/// Everything the user can decide, plus the polling memory of both release feeds. Repository URLs are
/// configuration rather than constants scattered through the code (SPEC.md §8.1).
/// </summary>
public sealed record ManagerSettings
{
    /// <summary>The manager's own release feed. Set during development for the real repository.</summary>
    public string ManagerRepository { get; set; } = "vladbogun1/zapret-by-grubeer";

    public string EngineRepository { get; set; } = "Flowseal/zapret-discord-youtube";

    public EngineRunMode RunMode { get; set; } = EngineRunMode.ManagedProcess;

    public bool StartEngineWithWindows { get; set; } = true;

    public bool CheckForUpdatesAutomatically { get; set; } = true;
    public bool NotifyAboutManagerUpdates { get; set; } = true;
    public bool NotifyAboutEngineUpdates { get; set; } = true;
    public bool AllowPreviewReleases { get; set; }

    /// <summary>Reserved by SPEC.md §8.1 and deliberately off: updates are never silent by default.</summary>
    public bool DownloadUpdatesAutomatically { get; set; }

    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>null follows the system theme; otherwise "light" or "dark".</summary>
    public string? ThemeOverride { get; set; }

    /// <summary>
    /// UI language as a BCP-47 tag. null follows Windows, falling back to English when the system
    /// language has no translation.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Set when the manager itself enabled TCP timestamps, so uninstall can offer to put the setting
    /// back. A value the manager did not change is never restored.
    /// </summary>
    public bool TcpTimestampsEnabledByManager { get; set; }

    public bool? TcpTimestampsValueBeforeManager { get; set; }

    /// <summary>
    /// Strategy remembered per connection, keyed by the one-way network fingerprint. A home connection and a
    /// mobile one usually need different strategies, and re-picking one by hand every time is exactly the
    /// friction this product exists to remove (SPEC.md §20).
    /// </summary>
    public Dictionary<string, string> NetworkStrategies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Services the user switched on, by catalog id. Absent means the manager adds nothing of its own to the
    /// user list — upstream's shipped lists already cover the common cases, so an empty selection is a valid
    /// and quiet default rather than something to be filled in.
    /// </summary>
    public List<string> EnabledServices { get; set; } = new();

    /// <summary>Services the user defined. Stored here rather than in a list file so they survive engine updates.</summary>
    public List<CustomServiceSetting> CustomServices { get; set; } = new();

    public ReleaseFeedState ManagerFeed { get; set; } = new();
    public ReleaseFeedState EngineFeed { get; set; } = new();

    public TimeSpan UpdateCheckInterval { get; set; } = TimeSpan.FromHours(6);
}

/// <summary>A user-defined service, in the shape the settings file stores it.</summary>
public sealed record CustomServiceSetting
{
    public string Id { get; set; } = string.Empty;
    public List<string> Domains { get; set; } = new();
    public string? CheckUrl { get; set; }
}

public interface ISettingsStore
{
    ManagerSettings Read();
    void Write(ManagerSettings settings);
    ManagerSettings Update(Action<ManagerSettings> mutate);
}

public sealed class SettingsStore(string? filePath = null, ILogger<SettingsStore>? logger = null) : ISettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path = filePath ?? AppPaths.SettingsFile;
    private readonly ILogger _logger = logger ?? NullLogger<SettingsStore>.Instance;
    private readonly object _gate = new();

    public ManagerSettings Read()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return new ManagerSettings();

            try
            {
                return JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(_path), Json) ?? new ManagerSettings();
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // Never let a damaged settings file stop the product; defaults are always usable.
                _logger.LogWarning(ex, "Could not read {Path}; falling back to default settings", _path);
                return new ManagerSettings();
            }
        }
    }

    public void Write(ManagerSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Json));

            if (File.Exists(_path)) File.Replace(temporary, _path, null);
            else File.Move(temporary, _path);
        }
    }

    public ManagerSettings Update(Action<ManagerSettings> mutate)
    {
        lock (_gate)
        {
            var settings = Read();
            mutate(settings);
            Write(settings);
            return settings;
        }
    }
}
