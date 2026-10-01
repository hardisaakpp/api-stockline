using System.Text.Json;
using Stockline.Domain.Identity;

namespace Stockline.Application.Identity;

public interface IUserPreferenceRepository
{
    Task<IReadOnlyList<UserPreference>> ListAsync(Guid userId, CancellationToken ct);

    Task<UserPreference?> GetAsync(Guid userId, string key, CancellationToken ct);

    Task SetAsync(Guid userId, string key, string json, CancellationToken ct);

    Task<bool> DeleteAsync(Guid userId, string key, CancellationToken ct);
}

public enum PreferenceOutcome
{
    Saved,
    InvalidKey,
    InvalidJson,
    TooLarge,
}

public sealed class UserPreferenceHandler(IUserPreferenceRepository preferences)
{
    public async Task<PreferenceOutcome> SetAsync(Guid userId, string key, JsonElement value, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > UserPreference.MaxKeyLength || key.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            return PreferenceOutcome.InvalidKey;
        }

        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return PreferenceOutcome.InvalidJson;
        }

        var json = value.GetRawText();
        if (json.Length > UserPreference.MaxJsonLength)
        {
            return PreferenceOutcome.TooLarge;
        }

        await preferences.SetAsync(userId, key, json, ct);
        return PreferenceOutcome.Saved;
    }
}
