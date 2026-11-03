// The database's model providers as chat sources, beside the config ones in ModelSourceRegistry.
//
// One OpenAiCompatibleSource per enabled row with the chat capability, built from the row and its
// decrypted key and held in memory. Every admin write calls ReloadAsync, so a saved, enabled,
// disabled or deleted provider shows in (or leaves) every picker on the next request, no restart.

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.ModelSources;

namespace RabbitIC.Api.ModelProviders;

/// <summary>What the rest of the app may know about a provider: never its key.</summary>
public sealed record ModelProviderRuntime(string Key, string Name, bool Enabled, bool Chat, string Status, string? Message, bool Speech = false);

public sealed partial class DbModelSourceProvider(
    IServiceScopeFactory scopes,
    ModelProviderKeys keys,
    IHttpClientFactory http,
    ModelUsageRecorder usage,
    ILoggerFactory logs)
{
    /// <summary>One client for every database provider: 120 s, no base address, no default headers.</summary>
    public const string HttpClientName = "model-providers";

    private sealed record Snapshot(
        IReadOnlyList<IChatModelSource> Sources,
        IReadOnlyDictionary<string, IChatModelSource> ByKey,
        IReadOnlyDictionary<string, ModelProviderRuntime> Runtime,
        IReadOnlyDictionary<string, (SpeechTarget Target, IReadOnlyList<string> Voices)> Speech)
    {
        public static readonly Snapshot Empty = new(
            Array.Empty<IChatModelSource>(),
            new Dictionary<string, IChatModelSource>(),
            new Dictionary<string, ModelProviderRuntime>(),
            new Dictionary<string, (SpeechTarget, IReadOnlyList<string>)>());
    }

    private volatile Snapshot _snapshot = Snapshot.Empty;
    private readonly SemaphoreSlim _reload = new(1, 1);
    private readonly ILogger _log = logs.CreateLogger<DbModelSourceProvider>();

    public IReadOnlyList<IChatModelSource> Sources => _snapshot.Sources;

    public IChatModelSource? Resolve(string key) =>
        _snapshot.ByKey.TryGetValue(key, out var s) ? s : null;

    /// <summary>The provider's name and status, or null when no row has this key.</summary>
    public ModelProviderRuntime? Runtime(string key) =>
        _snapshot.Runtime.TryGetValue(key, out var r) ? r : null;

    /// <summary>An enabled speech provider, with its decrypted key and stored voices; null when there is none by that key.</summary>
    public (SpeechTarget Target, IReadOnlyList<string> Voices)? Speech(string? key) =>
        key is not null && _snapshot.Speech.TryGetValue(key, out var s) ? s : null;

    /// <summary>Every provider with the speech capability (enabled or not), for Jiji's dropdown.</summary>
    public IReadOnlyList<ModelProviderRuntime> SpeechProviders =>
        _snapshot.Runtime.Values.Where(r => r.Speech).OrderBy(r => r.Name).ToList();

    /// <summary>Rebuilds every source from the table. Serialised, so two saves cannot interleave.</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _reload.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.ModelProviders.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);

            var sources = new List<IChatModelSource>();
            var runtime = new Dictionary<string, ModelProviderRuntime>(StringComparer.OrdinalIgnoreCase);
            var speech = new Dictionary<string, (SpeechTarget, IReadOnlyList<string>)>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var caps = ReadList(row.CapabilitiesJson);
                var chat = caps.Contains(ModelProviderCapabilities.Chat);
                var speaks = caps.Contains(ModelProviderCapabilities.Speech);
                if (!keys.TryUnprotect(row.ApiKeyProtected, out var apiKey))
                {
                    _log.LogWarning("Model provider {Key}: the stored key no longer decrypts (key ring changed). Paste it again in Settings.", row.Key);
                    runtime[row.Key] = new(row.Key, row.Name, row.Enabled, chat, ModelProviderStatus.KeyUnreadable,
                        "Key unreadable — paste it again.", speaks);
                    continue;
                }

                runtime[row.Key] = new(row.Key, row.Name, row.Enabled, chat,
                    row.Enabled ? row.LastTestStatus : ModelProviderStatus.Disabled, row.LastTestMessage, speaks);
                if (row.Enabled && chat) sources.Add(Build(row, apiKey));
                if (row.Enabled && speaks) speech[row.Key] = (SpeechTarget(row, apiKey), ReadList(row.VoicesJson));
            }

            _snapshot = new Snapshot(sources,
                sources.ToDictionary(s => s.SourceKey, StringComparer.OrdinalIgnoreCase), runtime, speech);
        }
        finally
        {
            _reload.Release();
        }
    }

    /// <summary>Where a row's speech API is; the base keeps its version segment ("…/v1/").</summary>
    public static SpeechTarget SpeechTarget(ModelProvider row, string? apiKey) =>
        new(row.Key, row.Name, new Uri(row.BaseUrl.TrimEnd('/') + "/"), apiKey);

    /// <summary>A source for one row and its plain key. Also used by the Test button, before the row is enabled.</summary>
    internal OpenAiCompatibleSource Build(ModelProvider row, string? apiKey, string? module = null, int? maxTokens = null)
    {
        var options = new OpenAiProviderOptions
        {
            Key = row.Key,
            DisplayName = row.Name,
            BaseUrl = row.BaseUrl,
            Models = ReadList(row.AllowedModelsJson).ToArray(),
            ToolCapableModels = ReadList(row.ToolCapableModelsJson).ToArray(),
            // A keyless server on the LAN costs nothing per call; anything with a key is metered.
            Metered = apiKey is not null,
            Multipliers = Multipliers(row),
        };
        var multipliers = options.Multipliers;
        var baseUri = new Uri(row.BaseUrl.TrimEnd('/') + "/");
        var key = row.Key;
        var name = row.Name;
        var streamUsage = row.StreamUsage;
        var log = logs.CreateLogger<OpenAiCompatibleSource>();

        return new OpenAiCompatibleSource(options, log, args => new OpenAiCall(
            http.CreateClient(HttpClientName),
            BaseUri: baseUri,
            ApiKey: apiKey,
            ProviderName: name,
            Stream: args.Stream,
            IncludeUsage: streamUsage,
            RetryTransient: true,
            Temperature: args.Temperature,
            MaxTokens: maxTokens,
            OnDone: outcome => Done(key, module ?? args.Module ?? "chat", outcome, args.UserId,
                multipliers.TryGetValue(outcome.Model, out var x) ? x : DefaultMultiplier)));
    }

    /// <summary>One usage row per call, and the provider's status when a real call changes it.</summary>
    internal void Done(string key, string module, OpenAiCallOutcome outcome, int? userId = null, double multiplier = DefaultMultiplier)
    {
        usage.Record(new ModelProviderUsage
        {
            ProviderKey = key,
            Model = outcome.Model,
            Module = module,
            UserId = userId,
            PromptTokens = outcome.PromptTokens,
            CompletionTokens = outcome.CompletionTokens,
            WeightedTokens = (outcome.PromptTokens + outcome.CompletionTokens) * multiplier,
            Status = outcome.Status,
        });

        var status = StatusFor(outcome.Status);
        if (status is null) return;
        var snap = _snapshot;
        if (!snap.Runtime.TryGetValue(key, out var current) || current.Status == status) return;

        var message = status == ModelProviderStatus.Ok ? null : MessageFor(status, current.Name);
        var runtime = new Dictionary<string, ModelProviderRuntime>(snap.Runtime, StringComparer.OrdinalIgnoreCase)
        {
            [key] = current with { Status = status, Message = message },
        };
        _snapshot = snap with { Runtime = runtime };
        usage.RecordStatus(key, status, message);
    }

    /// <summary>The status a call outcome implies, or null when it says nothing about the provider (a 400, a cancel).</summary>
    public static string? StatusFor(string outcome) => outcome switch
    {
        "ok" => ModelProviderStatus.Ok,
        "401" or "403" => ModelProviderStatus.KeyRejected,
        "402" => ModelProviderStatus.NoCredit,
        "429" => ModelProviderStatus.RateLimited,
        "unreachable" => ModelProviderStatus.Unreachable,
        _ => null,
    };

    public static string MessageFor(string status, string name) => status switch
    {
        ModelProviderStatus.KeyRejected => $"{name} rejected the API key — paste a new one.",
        ModelProviderStatus.NoCredit => $"{name} credit used up — top up.",
        ModelProviderStatus.RateLimited => $"{name} is rate-limiting this account.",
        ModelProviderStatus.Unreachable => $"{name} could not be reached.",
        ModelProviderStatus.KeyUnreadable => "Key unreadable — paste it again.",
        _ => $"{name} reported an error.",
    };

    /// <summary>A model with no multiplier in the catalog costs what the plan counts by default.</summary>
    public const double DefaultMultiplier = 1.0;

    [System.Text.RegularExpressions.GeneratedRegex(@"\(\s*(\d+(?:\.\d+)?)\s*x\s*\)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex MultiplierInName();

    /// <summary>The "(0.2x)" at the end of a catalog name as 0.2; null when there is none.</summary>
    public static double? ParseMultiplier(string? name)
    {
        var m = MultiplierInName().Match(name ?? "");
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Every catalog model's multiplier, from the stored value or, for an older catalog, its name.</summary>
    public static Dictionary<string, double> Multipliers(ModelProvider row)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in ReadCatalog(row))
            if ((m.Multiplier ?? ParseMultiplier(m.Name)) is { } x) map[m.Id] = x;
        return map;
    }

    private static readonly JsonSerializerOptions CatalogJson = new(JsonSerializerDefaults.Web);

    public static List<ModelCatalogEntry> ReadCatalog(ModelProvider row)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<ModelCatalogEntry>>(row.CatalogJson, CatalogJson) ?? new())
                .Select(m => m.Multiplier is null && ParseMultiplier(m.Name) is { } x ? m with { Multiplier = x } : m)
                .ToList();
        }
        catch (JsonException) { return new(); }
    }

    public static List<string> ReadList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json)?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new(); }
        catch (JsonException) { return new(); }
    }
}
