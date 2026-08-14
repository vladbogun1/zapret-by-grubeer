using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zapret.Core.Engine;

/// <summary>
/// Contents of <c>runtime\current.json</c>: which build is active, which one it can fall back to,
/// and enough context to explain the state to a user. docs/flowseal-compatibility.md §6.
/// </summary>
public sealed record EngineCurrentState
{
    [JsonPropertyName("current")] public string? Current { get; init; }
    [JsonPropertyName("previous")] public string? Previous { get; init; }
    [JsonPropertyName("activatedUtc")] public DateTimeOffset? ActivatedUtc { get; init; }
    [JsonPropertyName("activatedBy")] public string? ActivatedBy { get; init; }
    [JsonPropertyName("versionSource")] public string? VersionSource { get; init; }
    [JsonPropertyName("strategyId")] public string? StrategyId { get; init; }
    [JsonPropertyName("strategyCount")] public int StrategyCount { get; init; }
    [JsonPropertyName("rejectedTags")] public List<string> RejectedTags { get; init; } = new();

    public static EngineCurrentState Empty { get; } = new();
}

public interface IEngineStateStore
{
    EngineCurrentState Read();
    void Write(EngineCurrentState state);
}

public sealed class EngineStateStore(string filePath, ILogger<EngineStateStore>? logger = null) : IEngineStateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ILogger _logger = logger ?? NullLogger<EngineStateStore>.Instance;
    private readonly object _gate = new();

    public EngineCurrentState Read()
    {
        lock (_gate)
        {
            if (!File.Exists(filePath)) return EngineCurrentState.Empty;

            try
            {
                return JsonSerializer.Deserialize<EngineCurrentState>(File.ReadAllText(filePath), Json)
                       ?? EngineCurrentState.Empty;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // A corrupt state file must not stop the manager: the directories on disk are the truth.
                _logger.LogWarning(ex, "Could not read {Path}; treating engine state as unknown", filePath);
                return EngineCurrentState.Empty;
            }
        }
    }

    public void Write(EngineCurrentState state)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            // Write-then-replace, so a crash mid-write cannot leave an unreadable state file.
            var temporary = filePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Json));

            if (File.Exists(filePath)) File.Replace(temporary, filePath, null);
            else File.Move(temporary, filePath);
        }
    }
}
