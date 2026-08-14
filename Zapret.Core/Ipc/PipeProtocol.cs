using System.Text;
using System.Text.Json;

namespace Zapret.Core.Ipc;

/// <summary>
/// Framing for the named-pipe channel: one JSON document per line, UTF-8, no trailing whitespace.
/// Deliberately boring — no polymorphic type resolution, so a malformed message can never make the
/// service construct an unexpected type.
/// </summary>
public static class PipeProtocol
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>A single request/response must never exceed this. Anything larger is a client bug.</summary>
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    public static async Task WriteMessageAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, Json);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");

        if (bytes.Length > MaxMessageBytes)
        {
            throw new InvalidOperationException($"IPC message of {bytes.Length} bytes exceeds the {MaxMessageBytes} byte limit");
        }

        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one newline-terminated message, or null when the peer closed the pipe.</summary>
    public static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var accumulated = new MemoryStream();

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return accumulated.Length == 0 ? null : Encoding.UTF8.GetString(accumulated.ToArray()).TrimEnd('\n');

            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    accumulated.Write(buffer, 0, i);
                    return Encoding.UTF8.GetString(accumulated.ToArray());
                }
            }

            accumulated.Write(buffer, 0, read);

            if (accumulated.Length > MaxMessageBytes)
            {
                throw new InvalidOperationException("IPC message exceeded the size limit before a newline arrived");
            }
        }
    }

    public static JsonElement ToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, Json);

    public static T? FromElement<T>(JsonElement? element) =>
        element is null ? default : element.Value.Deserialize<T>(Json);
}
