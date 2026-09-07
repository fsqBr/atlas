using System.IO.Compression;
using System.Text.Json;
using Atlas.Connector.Abstractions;
using Atlas.Domain.Sources;

namespace Atlas.Connector.Upload;

public sealed class UploadOptions
{
    public const string SectionName = "Atlas:Uploads";

    /// <summary>Shared volume: the API writes archives here, the worker reads them.</summary>
    public string Directory { get; set; } = "/var/atlas/uploads";

    public long MaxArchiveBytes { get; set; } = 1024L * 1024 * 1024;

    public long MaxExtractedBytes { get; set; } = 4096L * 1024 * 1024;

    public int MaxEntries { get; set; } = 300_000;

    /// <summary>Unreferenced archives younger than this are kept (an upload may precede its assessment).</summary>
    public int OrphanRetentionHours { get; set; } = 24;
}

/// <summary>What the API stores next to an uploaded archive.</summary>
public sealed record UploadManifest(string Id, string Name, long Bytes, int Files, DateTimeOffset UploadedAtUtc);

/// <summary>
/// Source kind "upload": a folder the user picked in the browser, zipped
/// client-side and posted to the API. Materialization extracts the archive into
/// the workspace with zip-slip protection and size caps — the archive is
/// hostile input like any repository. Nothing is executed.
/// </summary>
public sealed class UploadConnector(UploadOptions options) : ISourceConnector
{
    public ConnectorDescriptor Descriptor { get; } = new(
        Id: "connector.upload",
        Name: "Browser upload",
        Version: "0.1.0",
        Capabilities: ["materialize", "upload"]);

    public bool CanHandle(SourceReference source) => source.Kind == SourceReference.Kinds.Upload;

    public Task<IReadOnlyList<RepositoryInfo>> DiscoverRepositoriesAsync(SourceReference source, CancellationToken cancellationToken)
    {
        var manifest = ReadManifest(source.Locator);
        IReadOnlyList<RepositoryInfo> result = [new RepositoryInfo(manifest?.Name ?? source.Locator, source.Locator, SourceReference.Kinds.Upload)];
        return Task.FromResult(result);
    }

    public Task<MaterializedSource> MaterializeAsync(SourceReference source, string targetDirectory, CancellationToken cancellationToken)
    {
        var archive = ArchivePath(options, source.Locator);
        if (!File.Exists(archive))
        {
            throw new FileNotFoundException($"Upload '{source.Locator}' was not found (uploads are kept on the atlas-uploads volume).", archive);
        }

        System.IO.Directory.CreateDirectory(targetDirectory);
        Extract(archive, targetDirectory, options, cancellationToken);
        return Task.FromResult(new MaterializedSource(targetDirectory, IsBorrowed: false, CommitSha: null));
    }

    public UploadManifest? ReadManifest(string id)
    {
        var path = ManifestPath(options, id);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<UploadManifest>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string ArchivePath(UploadOptions options, string id) => Path.Combine(options.Directory, ValidateId(id) + ".zip");

    public static string ManifestPath(UploadOptions options, string id) => Path.Combine(options.Directory, ValidateId(id) + ".json");

    public static string ValidateId(string id) =>
        Guid.TryParse(id, out var guid) ? guid.ToString("N") : throw new ArgumentException($"Upload id '{id}' is not a GUID.", nameof(id));

    /// <summary>Extracts with containment (no absolute paths, no '..', no symlinks) and size/entry caps.</summary>
    internal static void Extract(string archivePath, string targetDirectory, UploadOptions options, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(targetDirectory);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > options.MaxEntries)
        {
            throw new InvalidDataException($"Archive has {archive.Entries.Count:N0} entries, above the limit of {options.MaxEntries:N0}.");
        }

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.FullName.Replace('\\', '/');
            if (relative.Length == 0 || relative.EndsWith('/'))
            {
                continue; // directory entry
            }

            if (Path.IsPathRooted(relative) || relative.Split('/').Any(s => s is ".." or "."))
            {
                throw new InvalidDataException($"Archive entry '{entry.FullName}' escapes the workspace.");
            }

            total += entry.Length;
            if (total > options.MaxExtractedBytes)
            {
                throw new InvalidDataException($"Archive expands beyond {options.MaxExtractedBytes / (1024 * 1024):N0} MB.");
            }

            var destination = Path.GetFullPath(Path.Combine(root, relative));
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && destination != root)
            {
                throw new InvalidDataException($"Archive entry '{entry.FullName}' escapes the workspace.");
            }

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }
}
