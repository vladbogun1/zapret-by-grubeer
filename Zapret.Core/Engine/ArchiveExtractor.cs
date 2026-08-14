using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zapret.Core.Engine;

public sealed record ExtractionResult(bool Success, string? Directory, string? Error);

/// <summary>
/// Extracts an upstream release archive into a candidate directory. Archive contents are untrusted
/// input: entry paths are validated, and a single wrapper folder is flattened so the candidate
/// directory always has <c>bin\</c> at its root regardless of how upstream packed the release.
/// </summary>
public sealed class ArchiveExtractor(ILogger<ArchiveExtractor>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<ArchiveExtractor>.Instance;

    public ExtractionResult Extract(string archivePath, string destinationDirectory)
    {
        try
        {
            if (Directory.Exists(destinationDirectory)) Directory.Delete(destinationDirectory, recursive: true);
            Directory.CreateDirectory(destinationDirectory);

            var root = Path.GetFullPath(destinationDirectory);

            using (var archive = ZipFile.OpenRead(archivePath))
            {
                foreach (var entry in archive.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(root, entry.FullName));

                    if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        return new ExtractionResult(false, null,
                            $"the archive contains an entry that would be written outside the target directory: {entry.FullName}");
                    }

                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }

            var effective = Flatten(root);
            _logger.LogInformation("Extracted {Archive} to {Directory}", Path.GetFileName(archivePath), effective);
            return new ExtractionResult(true, effective, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Extraction of {Archive} failed", archivePath);
            return new ExtractionResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// If the archive wrapped everything in one folder, move that folder's contents up. Upstream's
    /// zip is currently flat, but a future release packing a wrapper must not break the manager.
    /// </summary>
    private string Flatten(string root)
    {
        if (Directory.EnumerateFiles(root).Any()) return root;

        var directories = Directory.EnumerateDirectories(root).ToList();
        if (directories.Count != 1) return root;

        var inner = directories[0];

        foreach (var file in Directory.EnumerateFiles(inner))
        {
            File.Move(file, Path.Combine(root, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(inner))
        {
            var destination = Path.Combine(root, Path.GetFileName(directory));
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.Move(directory, destination);
        }

        Directory.Delete(inner, recursive: true);
        _logger.LogInformation("Flattened a single wrapper folder from the archive");
        return root;
    }
}
