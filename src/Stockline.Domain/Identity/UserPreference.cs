namespace Stockline.Domain.Identity;

/// <summary>A per-user UI preference (column layout, sidebar state...) stored as an opaque JSON document.</summary>
public sealed class UserPreference
{
    public const int MaxKeyLength = 100;
    public const int MaxJsonLength = 20_000;

    // Parameterless constructor required by EF Core materialization.
    private UserPreference()
    {
        Key = null!;
        Json = null!;
    }

    public UserPreference(Guid userId, string key, string json)
    {
        UserId = userId;
        Key = key;
        Json = json;
    }

    public Guid UserId { get; }

    public string Key { get; }

    public string Json { get; private set; }

    public void Replace(string json) => Json = json;
}
