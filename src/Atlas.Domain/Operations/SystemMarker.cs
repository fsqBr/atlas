namespace Atlas.Domain.Operations;

/// <summary>
/// A tiny durable key/value for cross-replica coordination (e.g. "the weekly digest was sent at X"):
/// in-memory state duplicates work the moment a second worker replica exists or a pod restarts.
/// </summary>
public sealed class SystemMarker
{
    public string Key { get; private set; } = null!;
    public string Value { get; private set; } = null!;
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private SystemMarker()
    {
    }

    public SystemMarker(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Marker key must not be empty.", nameof(key));
        }

        Key = key.Trim();
        Set(value);
    }

    public void Set(string value)
    {
        Value = value ?? string.Empty;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}
