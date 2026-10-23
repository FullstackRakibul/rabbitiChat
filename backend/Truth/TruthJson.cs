using System.Text.Json;
using System.Text.Json.Nodes;

namespace RabbitIC.Api.Truth;

/// <summary>The JSON columns of the Tr* tables: string lists and the hand-verification log.</summary>
public static class TruthJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static List<string> Strings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json, Options)?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new(); }
        catch (JsonException) { return new(); }
    }

    public static string FromStrings(IEnumerable<string>? values) =>
        JsonSerializer.Serialize((values ?? Enumerable.Empty<string>()).Select(v => v.Trim()).Where(v => v.Length > 0).Distinct().ToList(), Options);

    public sealed record HandVerified(string Token, string Note, int ByUserId, DateTime AtUtc);

    public static List<HandVerified> Hand(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<HandVerified>>(json, Options) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public static string FromHand(IEnumerable<HandVerified> list) => JsonSerializer.Serialize(list, Options);

    /// <summary>UTC "2026-10-21T14:00:00Z", as the snapshot and the admin API write every time.</summary>
    public static string? Utc(DateTime? value) => value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    public static JsonNode? Node(object? value) => JsonSerializer.SerializeToNode(value, Options);
}
