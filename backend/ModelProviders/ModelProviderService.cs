// Model providers — every rule of the admin screen, in one place. The endpoints only map
// the outcome to a status code.
//
//   · The key is write-only: stored encrypted, returned only as its last 4, and a blank key
//     on edit keeps the stored one.
//   · A database provider never takes a key a config or built-in source already has.
//   · Every write reloads DbModelSourceProvider, so pickers change on the next request.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitIC.Api.Data;
using RabbitIC.Api.Jiji;
using RabbitIC.Api.ModelSources;

namespace RabbitIC.Api.ModelProviders;

public enum ProviderOutcome { Ok, NotFound, Invalid, Conflict }

public sealed record ProviderResult<T>(ProviderOutcome Outcome, T? Value = default, string? Error = null,
    Dictionary<string, string>? Errors = null);

public sealed partial class ModelProviderService(
    AppDbContext db,
    ModelProviderKeys keys,
    DbModelSourceProvider sources,
    IModelSourceRegistry registry,
    IConfiguration config,
    Jiji.Services.JijiSettingsService jijiSettings,
    SpeechClient speech,
    IHttpClientFactory http,
    ILogger<ModelProviderService> log)
{
    private const int MaxCatalog = 2000;

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}$")]
    private static partial Regex KeyShape();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- Read ----

    public async Task<IReadOnlyList<ModelProviderDto>> ListAsync(CancellationToken ct)
    {
        var rows = await db.ModelProviders.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        var usage = await MonthUsageAsync(null, ct);
        var jiji = await jijiSettings.GetAsync(ct);
        return rows.Select(r => ToDto(r, usage.GetValueOrDefault(r.Key), jiji)).ToList();
    }

    /// <summary>This month's tokens per provider (from the 1st, Asia/Dhaka, as the caps); with a module, only that module's calls.</summary>
    public async Task<Dictionary<string, ModelProviderMonthUsage>> MonthUsageAsync(string? module, CancellationToken ct)
    {
        var start = ModelAccess.MonthStartUtc();
        var q = db.ModelProviderUsages.AsNoTracking().Where(u => u.CreatedAt >= start);
        if (module is not null) q = q.Where(u => u.Module == module);
        var rows = await q.GroupBy(u => u.ProviderKey)
            .Select(g => new { g.Key, P = g.Sum(x => (long)x.PromptTokens), C = g.Sum(x => (long)x.CompletionTokens), N = g.Count(), W = g.Sum(x => x.WeightedTokens) })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => new ModelProviderMonthUsage(r.P, r.C, r.P + r.C, r.N, Math.Round(r.W, 1)), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ProviderResult<ModelCatalogDto>> CatalogAsync(int id, CancellationToken ct)
    {
        var row = await db.ModelProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return row is null ? new(ProviderOutcome.NotFound, Error: "No such provider.") : new(ProviderOutcome.Ok, ToCatalog(row));
    }

    // ---- Write ----

    public async Task<ProviderResult<ModelProviderDto>> CreateAsync(ModelProviderInput input, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var key = input.Key?.Trim().ToLowerInvariant() ?? "";
        if (!KeyShape().IsMatch(key))
            errors["key"] = "2–32 characters: lowercase letters, digits and '-', starting with a letter.";
        else if (ReservedKeys().Contains(key))
            errors["key"] = $"'{key}' is already a built-in or config-file model source.";
        else if (await db.ModelProviders.AnyAsync(p => p.Key == key, ct))
            errors["key"] = $"A provider with the key '{key}' already exists.";

        var row = new ModelProvider { Key = key };
        Apply(row, input, isNew: true, errors);
        if (errors.Count > 0) return new(ProviderOutcome.Invalid, Error: errors.Values.First(), Errors: errors);

        db.ModelProviders.Add(row);
        await db.SaveChangesAsync(ct);
        await sources.ReloadAsync(ct);
        log.LogInformation("Model provider {Key} added ({Host}).", row.Key, new Uri(row.BaseUrl).Host);
        return new(ProviderOutcome.Ok, await DtoAsync(row, ct));
    }

    public async Task<ProviderResult<ModelProviderDto>> UpdateAsync(int id, ModelProviderInput input, CancellationToken ct)
    {
        var row = await db.ModelProviders.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (row is null) return new(ProviderOutcome.NotFound, Error: "No such provider.");

        var errors = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(input.Key) && !input.Key.Trim().Equals(row.Key, StringComparison.OrdinalIgnoreCase))
            errors["key"] = "The key cannot change: usage history and Jiji's settings refer to it. Add a new provider instead.";
        var oldBase = row.BaseUrl;
        Apply(row, input, isNew: false, errors);
        if (errors.Count > 0) return new(ProviderOutcome.Invalid, Error: errors.Values.First(), Errors: errors);

        // A new address or key has not been tested.
        if (!string.IsNullOrWhiteSpace(input.ApiKey) || input.ClearApiKey == true || row.BaseUrl != oldBase)
        {
            row.LastTestStatus = ModelProviderStatus.Untested;
            row.LastTestMessage = null;
        }
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await sources.ReloadAsync(ct);
        return new(ProviderOutcome.Ok, await DtoAsync(row, ct));
    }

    public async Task<ProviderResult<bool>> DeleteAsync(int id, CancellationToken ct)
    {
        var row = await db.ModelProviders.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (row is null) return new(ProviderOutcome.NotFound, Error: "No such provider.");
        if (UsedByJiji(row.Key, await jijiSettings.GetAsync(ct)))
            return new(ProviderOutcome.Conflict, Error: $"Jiji's settings use '{row.Key}' (script model or speech provider). Choose another in Jiji → Settings first.");

        db.ModelProviders.Remove(row);
        await db.SaveChangesAsync(ct);
        await sources.ReloadAsync(ct);
        return new(ProviderOutcome.Ok, true);
    }

    public async Task<ProviderResult<ModelCatalogDto>> SaveModelsAsync(int id, ModelProviderModelsInput input, CancellationToken ct)
    {
        var row = await db.ModelProviders.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (row is null) return new(ProviderOutcome.NotFound, Error: "No such provider.");

        var allowed = Clean(input.Allowed);
        var tools = Clean(input.ToolCapable);
        var catalog = ReadCatalog(row).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        // A server without a catalog (GET /models unsupported) takes ids as typed.
        var unknown = catalog.Count == 0 ? new List<string>() : allowed.Where(m => !catalog.Contains(m)).ToList();
        if (unknown.Count > 0)
            return new(ProviderOutcome.Invalid, Error: $"Not in the catalog: {string.Join(", ", unknown.Take(5))}. Refresh the catalog first.");
        var notAllowed = tools.Where(t => !allowed.Contains(t)).ToList();
        if (notAllowed.Count > 0)
            return new(ProviderOutcome.Invalid, Error: $"Tools can only be on for allowed models: {string.Join(", ", notAllowed.Take(5))}.");

        row.AllowedModelsJson = JsonSerializer.Serialize(allowed);
        row.ToolCapableModelsJson = JsonSerializer.Serialize(tools);
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await sources.ReloadAsync(ct);
        return new(ProviderOutcome.Ok, ToCatalog(row));
    }

    // ---- Talking to the provider ----

    public async Task<ProviderResult<ModelCatalogDto>> RefreshCatalogAsync(int id, CancellationToken ct)
    {
        var row = await db.ModelProviders.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (row is null) return new(ProviderOutcome.NotFound, Error: "No such provider.");
        if (!keys.TryUnprotect(row.ApiKeyProtected, out var apiKey))
            return new(ProviderOutcome.Invalid, Error: "Key unreadable — paste it again.");

        var caps = DbModelSourceProvider.ReadList(row.CapabilitiesJson);
        if (caps.Contains(ModelProviderCapabilities.Speech))
        {
            // A speech provider's catalog is its voice list.
            try
            {
                var voices = await speech.VoicesAsync(DbModelSourceProvider.SpeechTarget(row, apiKey), ct);
                if (voices is null && !Textual(caps))
                    return new(ProviderOutcome.Invalid, Error: $"{row.Name} has no GET /audio/voices; its voice list cannot be read.");
                if (voices is not null) row.VoicesJson = JsonSerializer.Serialize(voices);
            }
            catch (SpeechException e) { return new(ProviderOutcome.Invalid, Error: e.Message); }
        }
        if (Textual(caps))
        {
            var (models, status, message) = await FetchModelsAsync(row, apiKey, ct);
            if (models is null) return new(ProviderOutcome.Invalid, Error: message ?? DbModelSourceProvider.MessageFor(status, row.Name));
            StoreCatalog(row, models);
        }
        row.CatalogFetchedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await sources.ReloadAsync(ct);
        return new(ProviderOutcome.Ok, ToCatalog(row));
    }

    /// <summary>
    /// GET /models, then a 5-token chat call with "stream": false, then the same streamed — the way
    /// Chat calls it. Each step is reported; the provider's status is what the first failure says.
    /// </summary>
    public async Task<ProviderResult<ModelProviderTestDto>> TestAsync(int id, CancellationToken ct)
    {
        var row = await db.ModelProviders.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (row is null) return new(ProviderOutcome.NotFound, Error: "No such provider.");

        var steps = new List<ModelProviderTestStep>();
        string status = ModelProviderStatus.Ok;
        string? message = null;

        if (!keys.TryUnprotect(row.ApiKeyProtected, out var apiKey))
        {
            status = ModelProviderStatus.KeyUnreadable;
            message = "Key unreadable — paste it again.";
            steps.Add(new("Key", false, message));
            return await FinishTestAsync(row, status, message, steps, ct);
        }

        // A speech provider: its voices, then one short WAV. One that also chats goes on to the chat steps.
        var caps = DbModelSourceProvider.ReadList(row.CapabilitiesJson);
        if (caps.Contains(ModelProviderCapabilities.Speech))
        {
            var (speechStatus, speechMessage) = await TestSpeechAsync(row, apiKey, steps, ct);
            if (!Textual(caps) || speechStatus != ModelProviderStatus.Ok)
                return await FinishTestAsync(row, speechStatus, speechMessage, steps, ct);
        }

        // 1. The model list.
        var (models, listStatus, listMessage) = await FetchModelsAsync(row, apiKey, ct);
        if (models is null)
        {
            steps.Add(new("GET /models", false, listMessage ?? listStatus));
            return await FinishTestAsync(row, listStatus, listMessage, steps, ct);
        }
        StoreCatalog(row, models);
        var speech = models.Where(m => LooksLikeSpeech(m)).Select(m => m.Id).Take(5).ToList();
        steps.Add(new("GET /models", true, $"{models.Count} models" + (speech.Count > 0 ? $"; speech-like: {string.Join(", ", speech)}" : "; none look like speech (TTS)")));

        if (!DbModelSourceProvider.ReadList(row.CapabilitiesJson).Contains(ModelProviderCapabilities.Chat))
            return await FinishTestAsync(row, status, message, steps, ct);

        var model = TestModel(row, models);
        if (model is null)
        {
            steps.Add(new("Chat", false, "No model to try: the catalog is empty. Tick a model and test again."));
            return await FinishTestAsync(row, ModelProviderStatus.Error, "No model to test chat with.", steps, ct);
        }

        // 2. One reply as plain JSON ("stream": false).
        var turns = new List<ChatTurn> { new("user", "Reply with the single word: ok") };
        var source = sources.Build(row, apiKey, module: "test", maxTokens: 5);
        var plain = await TryChatAsync(source, new ChatArgs(model, turns, Stream: false, Temperature: 0, Module: "test"), ct);
        if (plain.Error is not null)
        {
            steps.Add(new($"Chat (stream: false) · {model}", false, plain.Error.Message));
            var s = StatusForException(plain.Error);
            return await FinishTestAsync(row, s, plain.Error.Message, steps, ct);
        }
        steps.Add(new($"Chat (stream: false) · {model}", true, $"Answered “{Short(plain.Text)}”"));

        // 3. The same, streamed, with usage asked for. A provider that refuses stream_options
        //    is tried once without it; then streamed usage is simply not counted.
        var streamed = await TryChatAsync(source, new ChatArgs(model, turns, Temperature: 0, Module: "test"), ct);
        if (streamed.Error is { UpstreamStatus: 400 } && row.StreamUsage)
        {
            row.StreamUsage = false;
            source = sources.Build(row, apiKey, module: "test", maxTokens: 5);
            streamed = await TryChatAsync(source, new ChatArgs(model, turns, Temperature: 0, Module: "test"), ct);
            if (streamed.Error is null)
                steps.Add(new("Streamed usage", false, "The provider refused stream_options; streamed Chat replies will be counted as 0 tokens."));
        }
        if (streamed.Error is not null)
        {
            steps.Add(new("Chat (stream: true)", false, streamed.Error.Message));
            return await FinishTestAsync(row, StatusForException(streamed.Error), streamed.Error.Message, steps, ct);
        }
        steps.Add(new("Chat (stream: true)", true, $"Streamed “{Short(streamed.Text)}”"));

        return await FinishTestAsync(row, status, message, steps, ct);
    }

    private static bool Textual(List<string> caps) =>
        caps.Contains(ModelProviderCapabilities.Chat) || caps.Contains(ModelProviderCapabilities.Embeddings);

    /// <summary>GET /audio/voices (when the server has it), then POST /audio/speech "Test." as WAV. Pass = a non-empty WAV.</summary>
    private async Task<(string Status, string? Message)> TestSpeechAsync(ModelProvider row, string? apiKey, List<ModelProviderTestStep> steps, CancellationToken ct)
    {
        var target = DbModelSourceProvider.SpeechTarget(row, apiKey);
        List<string>? voices;
        try { voices = await speech.VoicesAsync(target, ct); }
        catch (SpeechException e)
        {
            steps.Add(new("GET /audio/voices", false, e.Message));
            return (SpeechStatus(e), e.Message);
        }
        if (voices is null) steps.Add(new("GET /audio/voices", true, "Not offered by this server; the stored voice list is kept."));
        else
        {
            row.VoicesJson = JsonSerializer.Serialize(voices);
            row.CatalogFetchedAt = DateTime.UtcNow;
            steps.Add(new("GET /audio/voices", true, $"{voices.Count} voices" + (voices.Count > 0 ? $": {string.Join(", ", voices.Take(6))}{(voices.Count > 6 ? "…" : "")}" : "")));
        }

        var stored = DbModelSourceProvider.ReadList(row.VoicesJson);
        var jiji = await jijiSettings.GetAsync(ct);
        var voice = jiji.Voice is { } v && stored.Contains(v) ? v : stored.FirstOrDefault();
        if (voice is null)
        {
            steps.Add(new("POST /audio/speech", false, "No voice to try: the server listed none."));
            return (ModelProviderStatus.Error, "No voice to test with.");
        }

        const string model = "kokoro";
        var step = $"POST /audio/speech · {model}, {voice}";
        try
        {
            var wav = await speech.SynthesizeAsync(target, model, voice, "Test.", 1.0, ct, TimeSpan.FromSeconds(90));
            Jiji.Services.JijiWavInfo info;
            try { info = Jiji.Services.JijiWav.Read(wav); }
            catch (InvalidDataException e)
            {
                steps.Add(new(step, false, $"Not a WAV: {e.Message}"));
                return (ModelProviderStatus.Error, $"{row.Name} did not return a WAV: {e.Message}");
            }
            if (info.DataBytes == 0)
            {
                steps.Add(new(step, false, "The WAV holds no audio."));
                return (ModelProviderStatus.Error, $"{row.Name} returned an empty WAV.");
            }
            steps.Add(new(step, true, $"{wav.Length:N0}-byte WAV, {info.SampleRate} Hz, {info.Channels} ch, {info.DurationMs / 1000:0.00} s"));
            return (ModelProviderStatus.Ok, null);
        }
        catch (SpeechException e)
        {
            steps.Add(new(step, false, e.Message));
            return (SpeechStatus(e), e.Message);
        }
    }

    private static string SpeechStatus(SpeechException e) =>
        e.Unreachable ? ModelProviderStatus.Unreachable
        : e.Status is { } code ? DbModelSourceProvider.StatusFor(code.ToString()) ?? ModelProviderStatus.Error
        : ModelProviderStatus.Error;

    private async Task<ProviderResult<ModelProviderTestDto>> FinishTestAsync(
        ModelProvider row, string status, string? message, List<ModelProviderTestStep> steps, CancellationToken ct)
    {
        if (status != ModelProviderStatus.KeyUnreadable)
        {
            row.LastTestAt = DateTime.UtcNow;
            row.LastTestStatus = status;
            row.LastTestMessage = message;
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        await sources.ReloadAsync(ct);
        var dto = await DtoAsync(row, ct);
        return new(ProviderOutcome.Ok, new ModelProviderTestDto(status == ModelProviderStatus.Ok, dto.Status, dto.StatusMessage ?? message, steps, dto));
    }

    private static async Task<(string Text, ChatSourceException? Error)> TryChatAsync(IChatModelSource source, ChatArgs args, CancellationToken ct)
    {
        var text = new System.Text.StringBuilder();
        try
        {
            await foreach (var e in source.StreamChatAsync(args with { }, ct))
                if (e.Kind == ChatEventKind.Content) text.Append(e.Text);
            return (text.ToString(), null);
        }
        catch (ChatSourceException ex) { return (text.ToString(), ex); }
    }

    private static string StatusForException(ChatSourceException ex) =>
        ex.UpstreamStatus is { } code
            ? DbModelSourceProvider.StatusFor(code.ToString()) ?? ModelProviderStatus.Error
            : ex.InnerException is HttpRequestException ? ModelProviderStatus.Unreachable : ModelProviderStatus.Error;

    /// <summary>GET {base}/models with the key on the request. Null models with a status when it failed.</summary>
    private async Task<(List<ModelCatalogEntry>? Models, string Status, string? Message)> FetchModelsAsync(
        ModelProvider row, string? apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(row.BaseUrl.TrimEnd('/') + "/"), "models"));
        if (apiKey is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            using var resp = await http.CreateClient(DbModelSourceProvider.HttpClientName).SendAsync(req, limit.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                log.LogWarning("Model provider {Key}: GET models answered {Status}.", row.Key, code);
                var status = DbModelSourceProvider.StatusFor(code.ToString()) ?? ModelProviderStatus.Error;
                return (null, status, status == ModelProviderStatus.Error
                    ? $"{row.Name} answered {code} to GET /models."
                    : DbModelSourceProvider.MessageFor(status, row.Name));
            }
            var body = await resp.Content.ReadAsStringAsync(limit.Token);
            var models = ParseCatalog(body);
            return models is null
                ? (null, ModelProviderStatus.Error, $"{row.Name}'s GET /models did not return an OpenAI-style model list.")
                : (models, ModelProviderStatus.Ok, null);
        }
        catch (HttpRequestException ex)
        {
            return (null, ModelProviderStatus.Unreachable, $"Cannot reach {new Uri(row.BaseUrl).Host}: {ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, ModelProviderStatus.Unreachable, $"{row.Name} did not answer GET /models within 30 seconds.");
        }
    }

    /// <summary>{ data: [ { id, name?, pricing? } ] } or a bare array; null when it is neither.</summary>
    internal static List<ModelCatalogEntry>? ParseCatalog(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var list = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Array ? m
                : (JsonElement?)null;
            if (list is null) return null;

            var result = new List<ModelCatalogEntry>();
            foreach (var e in list.Value.EnumerateArray())
            {
                if (result.Count >= MaxCatalog) break;
                var id = e.ValueKind == JsonValueKind.String ? e.GetString() : Str(e, "id");
                if (string.IsNullOrWhiteSpace(id) || id.Length > 200) continue;
                if (e.ValueKind != JsonValueKind.Object) { result.Add(new(id, null, null, null, null)); continue; }

                var name = Str(e, "name") ?? Str(e, "display_name");
                string? input = null, output = null;
                if (e.TryGetProperty("pricing", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    input = Str(p, "input") ?? Str(p, "prompt") ?? Str(p, "input_per_million");
                    output = Str(p, "output") ?? Str(p, "completion") ?? Str(p, "output_per_million");
                }
                input ??= Str(e, "input_price") ?? Str(e, "price_input");
                output ??= Str(e, "output_price") ?? Str(e, "price_output");
                var kind = Str(e, "type") ?? Str(e, "category") ?? Str(e, "mode");
                result.Add(new(id.Trim(), Trunc(name, 120), Trunc(input, 30), Trunc(output, 30), Trunc(kind, 40),
                    DbModelSourceProvider.ParseMultiplier(name) ?? Num(e, "multiplier") ?? Num(e, "cost_multiplier")));
            }
            return result.DistinctBy(m => m.Id).OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static double? Num(JsonElement e, string name) =>
        double.TryParse(Str(e, name)?.TrimEnd('x', 'X'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? Trunc(string? s, int n) => s is null ? null : s.Length <= n ? s : s[..n];

    private static bool LooksLikeSpeech(ModelCatalogEntry m) =>
        Regex.IsMatch($"{m.Id} {m.Kind}", @"\b(tts|speech|audio|voice)\b|tts", RegexOptions.IgnoreCase);

    /// <summary>The first allowed model, else the cheapest priced one, else the first listed.</summary>
    private static string? TestModel(ModelProvider row, List<ModelCatalogEntry> catalog)
    {
        var allowed = DbModelSourceProvider.ReadList(row.AllowedModelsJson);
        if (allowed.Count > 0) return allowed[0];
        var chatty = catalog.Where(m => !LooksLikeSpeech(m) && !ModelNaming.IsEmbeddingModel(m.Id)).ToList();
        return chatty
            .Select(m => (m.Id, Price: double.TryParse(m.InputPrice, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.MaxValue))
            .OrderBy(x => x.Price).Select(x => x.Id).FirstOrDefault();
    }

    // ---- Helpers ----

    private void Apply(ModelProvider row, ModelProviderInput input, bool isNew, Dictionary<string, string> errors)
    {
        var name = input.Name?.Trim();
        if (isNew || name is not null)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 60) errors["name"] = "A name of 1–60 characters.";
            else row.Name = name;
        }

        if (isNew || input.BaseUrl is not null)
        {
            var (url, local, error) = CheckBaseUrl(input.BaseUrl);
            if (error is not null) errors["baseUrl"] = error;
            else row.BaseUrl = url!;
            _ = local;
        }

        if (isNew || input.Capabilities is not null)
        {
            var caps = (input.Capabilities ?? Array.Empty<string>()).Select(c => c.Trim().ToLowerInvariant()).Distinct().ToList();
            if (caps.Count == 0 || caps.Any(c => !ModelProviderCapabilities.All.Contains(c)))
                errors["capabilities"] = "One or more of: chat, embeddings, speech.";
            else row.CapabilitiesJson = JsonSerializer.Serialize(caps);
        }

        var apiKey = input.ApiKey?.Trim();
        if (!string.IsNullOrEmpty(apiKey))
        {
            if (apiKey.Length > 500 || apiKey.Any(char.IsWhiteSpace))
                errors["apiKey"] = "The key has spaces or is too long — paste it again.";
            else
            {
                row.ApiKeyProtected = keys.Protect(apiKey);
                row.ApiKeyHint = ModelProviderKeys.Hint(apiKey);
            }
        }
        else if (input.ClearApiKey == true)
        {
            row.ApiKeyProtected = null;
            row.ApiKeyHint = null;
        }

        // Only a local server may go without a key.
        if (!errors.ContainsKey("baseUrl") && !errors.ContainsKey("apiKey") && row.ApiKeyProtected is null
            && !string.IsNullOrEmpty(row.BaseUrl) && !CheckBaseUrl(row.BaseUrl).Local)
            errors["apiKey"] = "Paste the API key. Only a server on this machine or the LAN may go without one.";

        if (input.Enabled is { } enabled) row.Enabled = enabled;
        if (input.StreamUsage is { } su) row.StreamUsage = su;
        if (input.MonthlyTokenBudget is { } budget)
        {
            if (budget < 0) errors["monthlyTokenBudget"] = "The budget cannot be negative.";
            else row.MonthlyTokenBudget = budget == 0 ? null : budget;
        }
    }

    /// <summary>https anywhere; http only for this machine or a private LAN address. No credentials, query or fragment.</summary>
    internal static (string? Url, bool Local, string? Error) CheckBaseUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
            return (null, false, "A full URL, e.g. https://api.mangomindbd.com/v1.");
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return (null, false, "The URL must start with https://.");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return (null, false, "No user name, query or fragment in the URL.");

        var local = IsLocalHost(uri.Host);
        if (uri.Scheme == Uri.UriSchemeHttp && !local)
            return (null, false, "Use https://. Plain http is allowed only for localhost or a LAN address.");
        return (uri.GetLeftPart(UriPartial.Path).TrimEnd('/'), local, null);
    }

    private static bool IsLocalHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>Built-in sources and every config-file provider, configured or not.</summary>
    private HashSet<string> ReservedKeys()
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "local", "cloud", "echo" };
        if (registry is ModelSourceRegistry r) reserved.UnionWith(r.RegisteredKeys);
        var configured = config.GetSection(OpenAiCompatibleOptions.SectionName).Get<OpenAiCompatibleOptions>();
        foreach (var p in configured?.Providers ?? new()) if (!string.IsNullOrWhiteSpace(p.Key)) reserved.Add(p.Key.Trim());
        return reserved;
    }

    private static bool UsedByJiji(string key, Jiji.Services.JijiEffectiveSettings jiji) =>
        (jiji.ScriptModel is not null && string.Equals(jiji.ScriptModelSource, key, StringComparison.OrdinalIgnoreCase))
        || string.Equals(jiji.SpeechProvider, key, StringComparison.OrdinalIgnoreCase);

    private static List<string> Clean(IReadOnlyList<string>? ids) =>
        (ids ?? Array.Empty<string>()).Select(i => i?.Trim() ?? "").Where(i => i.Length is > 0 and <= 200).Distinct(StringComparer.Ordinal).ToList();

    private static void StoreCatalog(ModelProvider row, List<ModelCatalogEntry> models)
    {
        row.CatalogJson = JsonSerializer.Serialize(models, Json);
        row.CatalogFetchedAt = DateTime.UtcNow;
        row.UpdatedAt = DateTime.UtcNow;
    }

    private static List<ModelCatalogEntry> ReadCatalog(ModelProvider row) => DbModelSourceProvider.ReadCatalog(row);

    private static ModelCatalogDto ToCatalog(ModelProvider row) => new(
        ReadCatalog(row), row.CatalogFetchedAt,
        DbModelSourceProvider.ReadList(row.AllowedModelsJson), DbModelSourceProvider.ReadList(row.ToolCapableModelsJson),
        DbModelSourceProvider.ReadList(row.VoicesJson));

    private async Task<ModelProviderDto> DtoAsync(ModelProvider row, CancellationToken ct) =>
        ToDto(row, (await MonthUsageAsync(null, ct)).GetValueOrDefault(row.Key), await jijiSettings.GetAsync(ct));

    private ModelProviderDto ToDto(ModelProvider r, ModelProviderMonthUsage? usage, Jiji.Services.JijiEffectiveSettings jiji)
    {
        // The runtime knows what the row cannot: a key that no longer decrypts, a status a real call just set.
        var rt = sources.Runtime(r.Key);
        var status = !r.Enabled ? ModelProviderStatus.Disabled : rt?.Status ?? r.LastTestStatus;
        var message = !r.Enabled ? null : rt?.Message ?? r.LastTestMessage;
        return new(
            r.Id, r.Key, r.Name, r.BaseUrl, r.ApiKeyProtected is not null, r.ApiKeyHint,
            DbModelSourceProvider.ReadList(r.CapabilitiesJson),
            DbModelSourceProvider.ReadList(r.AllowedModelsJson),
            DbModelSourceProvider.ReadList(r.ToolCapableModelsJson),
            ReadCatalog(r).Count, r.CatalogFetchedAt, r.StreamUsage, r.MonthlyTokenBudget, r.Enabled,
            status, message, r.LastTestAt,
            usage ?? new ModelProviderMonthUsage(0, 0, 0, 0),
            UsedByJiji(r.Key, jiji), r.CreatedAt, r.UpdatedAt, DbModelSourceProvider.ReadList(r.VoicesJson));
    }

    private static string Short(string s)
    {
        s = s.Trim();
        return s.Length <= 40 ? s : s[..40] + "…";
    }
}
