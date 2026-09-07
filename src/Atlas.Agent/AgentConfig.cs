using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atlas.Agent;

/// <summary>
/// What `atlas-agent install` remembers so the scheduled run needs no arguments: server, token, actor, window.
/// Stored under the user's profile (<c>~/.atlas-agent/config.json</c>), readable only by the user on Unix.
/// </summary>
public sealed class AgentConfig
{
    public string Server { get; set; } = "";

    public string Token { get; set; } = "";

    public string? Actor { get; set; }

    public bool Anonymous { get; set; }

    public int Days { get; set; } = 7;

    public string? Path { get; set; }

    /// <summary>Hours between scheduled runs (the scheduler granularity is coarse; 6–24 is sensible).</summary>
    public int EveryHours { get; set; } = 12;

    public DateTimeOffset InstalledAtUtc { get; set; }

    public DateTimeOffset? LastRunUtc { get; set; }

    public string? LastResult { get; set; }

    [JsonIgnore]
    public static string Directory => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".atlas-agent");

    [JsonIgnore]
    public static string FilePath => System.IO.Path.Combine(Directory, "config.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static AgentConfig? Load(string? path = null)
    {
        path ??= FilePath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        var dir = System.IO.Path.GetDirectoryName(path)!;
        System.IO.Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (IOException)
            {
            }
        }
    }

    public static string Serialize(AgentConfig config) => JsonSerializer.Serialize(config, Json);
}
