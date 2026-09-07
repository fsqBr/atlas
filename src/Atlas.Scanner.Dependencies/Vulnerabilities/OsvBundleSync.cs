using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Atlas.Scanner.Dependencies.Vulnerabilities;

public sealed record OsvBundleSyncResult(int Entries, long Bytes, DateTimeOffset SyncedAtUtc);

/// <summary>
/// Turns the OSV per-ecosystem export (a zip of one JSON document per
/// vulnerability, e.g. https://osv-vulnerabilities.storage.googleapis.com/NuGet/all.zip)
/// into the single JSON-array bundle Atlas reads offline. The download happens
/// in a component with egress (the API); scanners only ever read the file.
/// </summary>
public static class OsvBundleSync
{
    public const string DefaultNuGetUrl = "https://osv-vulnerabilities.storage.googleapis.com/NuGet/all.zip";
    public const string DefaultNpmUrl = "https://osv-vulnerabilities.storage.googleapis.com/npm/all.zip";

    /// <summary>Opt-in (large export): add to Atlas:Vulnerabilities:SyncUrls to enable Java CVE matching.</summary>
    public const string DefaultMavenUrl = "https://osv-vulnerabilities.storage.googleapis.com/Maven/all.zip";

    /// <summary>Opt-in: add to Atlas:Vulnerabilities:SyncUrls to enable Python CVE matching.</summary>
    public const string DefaultPyPiUrl = "https://osv-vulnerabilities.storage.googleapis.com/PyPI/all.zip";

    public static Task<OsvBundleSyncResult> SyncAsync(HttpClient http, string url, string targetPath, CancellationToken cancellationToken) =>
        SyncAsync(http, [url], targetPath, cancellationToken);

    /// <summary>Downloads several ecosystem exports and writes them as one JSON array (atomic replace).</summary>
    public static async Task<OsvBundleSyncResult> SyncAsync(
        HttpClient http, IReadOnlyList<string> urls, string targetPath, CancellationToken cancellationToken)
    {
        var zips = new List<MemoryStream>();
        try
        {
            foreach (var url in urls)
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                // Buffer the zip: ZipArchive needs a seekable stream.
                var zip = new MemoryStream();
                await response.Content.CopyToAsync(zip, cancellationToken);
                zip.Position = 0;
                zips.Add(zip);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
            var temp = targetPath + ".tmp";
            int entries;
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                entries = await WriteBundleAsync(zips, output, cancellationToken);
            }

            // Atomic replace so readers never see a half-written bundle.
            File.Move(temp, targetPath, overwrite: true);
            return new OsvBundleSyncResult(entries, new FileInfo(targetPath).Length, DateTimeOffset.UtcNow);
        }
        finally
        {
            foreach (var zip in zips)
            {
                await zip.DisposeAsync();
            }
        }
    }

    private static async Task<int> WriteBundleAsync(IReadOnlyList<MemoryStream> zips, Stream output, CancellationToken cancellationToken)
    {
        var count = 0;
        await output.WriteAsync("["u8.ToArray(), cancellationToken);
        foreach (var zip in zips)
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in archive.Entries.Where(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var entryStream = entry.Open();
                using var document = await TryParseAsync(entryStream, cancellationToken);
                if (document is null || document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("id", out _)
                    || document.RootElement.TryGetProperty("withdrawn", out _))
                {
                    continue;
                }

                if (count > 0)
                {
                    await output.WriteAsync(","u8.ToArray(), cancellationToken);
                }

                await output.WriteAsync(Compact(document.RootElement), cancellationToken);
                count++;
            }
        }

        await output.WriteAsync("]"u8.ToArray(), cancellationToken);
        await output.FlushAsync(cancellationToken);
        return count;
    }

    private static async Task<int> LegacySingleAsync(MemoryStream zip, string targetPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        var temp = targetPath + ".tmp";
        int entries;
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            entries = await WriteBundleAsync(zip, output, cancellationToken);
        }

        File.Move(temp, targetPath, overwrite: true);
        return entries;
    }

    /// <summary>Streams every *.json entry of the zip into one JSON array. Entries that are not valid JSON objects are skipped.</summary>
    public static async Task<int> WriteBundleAsync(Stream zipStream, Stream output, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var count = 0;

        await output.WriteAsync("["u8.ToArray(), cancellationToken);
        foreach (var entry in archive.Entries.Where(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var entryStream = entry.Open();
            using var document = await TryParseAsync(entryStream, cancellationToken);
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("id", out _))
            {
                continue;
            }

            if (count > 0)
            {
                await output.WriteAsync(","u8.ToArray(), cancellationToken);
            }

            await output.WriteAsync(Compact(document.RootElement), cancellationToken);
            count++;
        }

        await output.WriteAsync("]"u8.ToArray(), cancellationToken);
        await output.FlushAsync(cancellationToken);
        return count;
    }

    /// <summary>
    /// Keeps only what matching needs (id, summary, modified, aliases, severity, affected packages with versions and
    /// ranges); details, references and credits are dropped — the npm export shrinks roughly tenfold.
    /// </summary>
    internal static byte[] Compact(JsonElement entry)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var name in new[] { "id", "summary", "modified" })
            {
                if (entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    writer.WriteString(name, value.GetString());
                }
            }

            if (entry.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
            {
                writer.WritePropertyName("aliases");
                aliases.WriteTo(writer);
            }

            if (entry.TryGetProperty("database_specific", out var db) && db.ValueKind == JsonValueKind.Object && db.TryGetProperty("severity", out var severity))
            {
                writer.WriteStartObject("database_specific");
                writer.WritePropertyName("severity");
                severity.WriteTo(writer);
                writer.WriteEndObject();
            }
            else if (entry.TryGetProperty("severity", out var severities) && severities.ValueKind == JsonValueKind.Array)
            {
                writer.WritePropertyName("severity");
                severities.WriteTo(writer);
            }

            writer.WriteStartArray("affected");
            if (entry.TryGetProperty("affected", out var affectedList) && affectedList.ValueKind == JsonValueKind.Array)
            {
                foreach (var affected in affectedList.EnumerateArray())
                {
                    if (!affected.TryGetProperty("package", out var package))
                    {
                        continue;
                    }

                    writer.WriteStartObject();
                    writer.WritePropertyName("package");
                    package.WriteTo(writer);
                    if (affected.TryGetProperty("versions", out var versions))
                    {
                        writer.WritePropertyName("versions");
                        versions.WriteTo(writer);
                    }

                    if (affected.TryGetProperty("ranges", out var ranges))
                    {
                        writer.WritePropertyName("ranges");
                        ranges.WriteTo(writer);
                    }

                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static async Task<JsonDocument?> TryParseAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
