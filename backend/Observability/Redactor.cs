// Observability — redaction.
//
// A single IRedactor used by every log path: middleware, interceptor, logger
// provider, and client-event endpoint. "Default deny": any key matching the
// deny-list (case-insensitive) has its value replaced with ***, never truncated,
// hashed, or masked in any other way. The list covers passwords, tokens, API
// keys, cookies, and auth headers.

using System.Text.RegularExpressions;

namespace RabbitIC.Api.Observability;

public interface IRedactor
{
    /// <summary>
    /// Returns a redacted copy of a query string, replacing the values of
    /// sensitive keys with ***. The key names are preserved.
    /// </summary>
    string RedactQueryString(string? queryString);

    /// <summary>
    /// Returns a redacted copy of a JSON object string, replacing the values
    /// of sensitive property names with "***". Operates on the raw JSON text
    /// rather than parsing, so it never modifies structure or whitespace.
    /// </summary>
    string? RedactJson(string? json);

    /// <summary>
    /// Whether a property name is on the deny-list and its value must be ***.
    /// Used by the EF Core audit interceptor for individual property checks.
    /// </summary>
    bool IsSensitive(string propertyName);
}

public sealed class Redactor : IRedactor
{
    // Case-insensitive deny-list. Values are replaced with ***, never masked
    // in any other way, so a partial mask cannot leak a prefix.
    private static readonly HashSet<string> DenyList = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwordhash", "securitystamp", "concurrencystamp",
        "secret", "salt", "token", "access_token", "refresh_token",
        "apikey", "api_key", "authorization", "licensekey", "cookie",
        "keyhash",
    };

    // Matches a key=value pair where key is on the deny list (query strings).
    // Works on URL-encoded strings; values may be empty.
    private static readonly Regex QueryParamRegex = BuildQueryParamRegex();

    // Matches "key": "value" in JSON (double-quoted string values only).
    private static readonly Regex JsonStringPropertyRegex = BuildJsonStringRegex();

    // Matches "key": number|boolean|null in JSON.
    private static readonly Regex JsonScalarPropertyRegex = BuildJsonScalarRegex();

    public string RedactQueryString(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString)) return "";
        return QueryParamRegex.Replace(queryString, m =>
        {
            var key = m.Groups["key"].Value;
            var sep = m.Groups["sep"].Value;  // = or &key=
            var prefix = m.Groups["prefix"].Value;
            return $"{prefix}{key}=***";
        });
    }

    public string? RedactJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return json;
        var result = JsonStringPropertyRegex.Replace(json, m =>
        {
            var key = m.Groups["key"].Value;
            return IsSensitive(key) ? $"\"{key}\": \"***\"" : m.Value;
        });
        result = JsonScalarPropertyRegex.Replace(result, m =>
        {
            var key = m.Groups["key"].Value;
            return IsSensitive(key) ? $"\"{key}\": \"***\"" : m.Value;
        });
        return result;
    }

    public bool IsSensitive(string propertyName) =>
        DenyList.Contains(propertyName);

    // ---- Regex builders ----

    private static Regex BuildQueryParamRegex()
    {
        // Matches: [&?]<denyKey>=<value> capturing prefix, key, value
        var keys = string.Join("|", DenyList.Select(Regex.Escape));
        // prefix = & or ? or start, key = one of deny keys (case-insensitive)
        return new Regex(
            $@"(?<prefix>[&?]?)(?<key>{keys})=[^&]*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private static Regex BuildJsonStringRegex()
    {
        var keys = string.Join("|", DenyList.Select(Regex.Escape));
        return new Regex(
            $@"""(?<key>{keys})""\s*:\s*""(?:[^""\\]|\\.)*""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private static Regex BuildJsonScalarRegex()
    {
        var keys = string.Join("|", DenyList.Select(Regex.Escape));
        return new Regex(
            $@"""(?<key>{keys})""\s*:\s*(?<val>[^,}}\]\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
