// Secrets come from user-secrets (development) or the environment (Docker, fly) —
// never from appsettings*.json, which is committed. A secret-shaped value found in
// an appsettings file is discarded at startup: the key then holds what user-secrets,
// the environment or the command line give it, or nothing. The names of the keys
// discarded are logged; their values never are.

using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration.Json;

namespace RabbitIC.Api.Secrets;

public static partial class SecretConfig
{
    /// <summary>The last segment of a key that holds a secret: ApiKey, AuthToken, Password, ClientSecret … (not a bare "Key", which names a provider).</summary>
    [GeneratedRegex(@"(api[_-]?key|access[_-]?key|private[_-]?key|secret|token|password|passwd|pwd|credentials?|authorization)$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    /// <summary>Connection-string parameters that hold a secret.</summary>
    [GeneratedRegex(@"^(password|pwd|auth[_ ]?token|token|access[_ ]?token|secret)$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretParam();

    public static bool IsSecretKey(string key) => SecretName().IsMatch(ConfigurationPath.GetSectionKey(key));

    /// <summary>A connection string without its secret parameters (unchanged when it has none).</summary>
    public static string StripSecretParams(string connectionString)
    {
        try
        {
            var b = new DbConnectionStringBuilder { ConnectionString = connectionString };
            var secret = b.Keys.Cast<string>().Where(k => SecretParam().IsMatch(k)).ToList();
            if (secret.Count == 0) return connectionString;
            foreach (var k in secret) b.Remove(k);
            return b.ConnectionString;
        }
        catch (ArgumentException) { return connectionString; }
    }

    /// <summary>
    /// Discards every secret-shaped value the appsettings files carry. Returns the
    /// keys discarded, as "file:key".
    /// </summary>
    public static List<string> Scrub(ConfigurationManager config)
    {
        var providers = ((IConfigurationRoot)config).Providers.ToList();
        var others = providers.Where(p => !IsAppSettings(p)).ToList();
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var dropped = new List<string>();

        foreach (var p in providers.Where(IsAppSettings))
        {
            var file = Path.GetFileName(((JsonConfigurationProvider)p).Source.Path ?? "appsettings");
            foreach (var key in Keys(p, null))
            {
                if (!p.TryGet(key, out var value) || string.IsNullOrEmpty(value)) continue;
                string scrubbed;
                if (IsSecretKey(key)) scrubbed = "";
                else if (key.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase)
                         && StripSecretParams(value) is var s && s != value) scrubbed = s;
                else continue;

                dropped.Add($"{file}:{key}");
                // Another source's value wins as it did before; without one, the scrubbed value.
                string? other = null;
                foreach (var q in others)
                    if (q.TryGet(key, out var v) && !string.IsNullOrEmpty(v)) other = v;
                overrides[key] = other ?? scrubbed;
            }
        }

        // Added last, so it is read over everything before it.
        if (overrides.Count > 0) config.AddInMemoryCollection(overrides);
        return dropped;
    }

    private static bool IsAppSettings(IConfigurationProvider p) =>
        p is JsonConfigurationProvider j
        && Path.GetFileName(j.Source.Path ?? "").StartsWith("appsettings", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Keys(IConfigurationProvider p, string? parent)
    {
        foreach (var child in p.GetChildKeys(Enumerable.Empty<string>(), parent).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = parent is null ? child : ConfigurationPath.Combine(parent, child);
            yield return path;
            foreach (var k in Keys(p, path)) yield return k;
        }
    }
}
