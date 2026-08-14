using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zapret.Core.Testing;

/// <summary>
/// The outcome of one full sweep: which engine version and which network it was measured on, and the
/// ranked results. Both qualifiers matter — a strategy that wins on a home connection often loses on a
/// mobile one, and results from a previous engine version should not be presented as current.
/// </summary>
public sealed record TestSession
{
    [JsonPropertyName("completedUtc")] public DateTimeOffset CompletedUtc { get; init; }
    [JsonPropertyName("engineVersion")] public string? EngineVersion { get; init; }
    [JsonPropertyName("networkId")] public string? NetworkId { get; init; }
    [JsonPropertyName("results")] public IReadOnlyList<StrategyTestResult> Results { get; init; } = Array.Empty<StrategyTestResult>();

    /// <summary>Ranked best-first, so consumers never have to know the ordering rule.</summary>
    public IReadOnlyList<StrategyTestResult> Ranked() => StrategyTestParser.Rank(Results);

    public StrategyTestResult? Best() => StrategyTestParser.SelectBest(Results);
}

public interface ITestResultsStore
{
    TestSession? Read();
    void Write(TestSession session);
}

/// <summary>
/// Persists the last sweep so the dashboard still shows real numbers after a restart, instead of pretending
/// nothing was ever measured. One session is kept: an older sweep is not evidence about the current build.
/// </summary>
public sealed class TestResultsStore(string? filePath = null, ILogger<TestResultsStore>? logger = null) : ITestResultsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path = filePath ?? Path.Combine(AppPaths.Data, "test-results.json");
    private readonly ILogger _logger = logger ?? NullLogger<TestResultsStore>.Instance;
    private readonly object _gate = new();

    public TestSession? Read()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return null;

            try
            {
                return JsonSerializer.Deserialize<TestSession>(File.ReadAllText(_path), Json);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // Corrupt results are simply absent results; the UI then honestly says "not tested".
                _logger.LogWarning(ex, "Could not read {Path}; treating test results as absent", _path);
                return null;
            }
        }
    }

    public void Write(TestSession session)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(session, Json));

                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not persist test results to {Path}", _path);
            }
        }
    }
}
