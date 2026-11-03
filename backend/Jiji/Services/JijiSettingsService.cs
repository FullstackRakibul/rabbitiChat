// Jiji — its settings: the row SystemAdmin edits in Jiji → Settings, with the Jiji:* config values
// as fallbacks for anything left blank. Read by the generators, the narration job and the status
// card, so it is cached and the cache is dropped on save.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using RabbitIC.Api.Data;
using RabbitIC.Api.Jiji.Models;
using RabbitIC.Api.ModelProviders;
using RabbitIC.Api.Models;
using RabbitIC.Api.Rbac;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.Jiji.Services;

/// <summary>What Jiji uses now: the stored value, else the config fallback.</summary>
public sealed record JijiEffectiveSettings(
    string? ScriptModel, string ScriptModelSource, string? SpeechProvider, string SpeechModel, string? Voice,
    double Speed, int GapMs);

/// <summary>A save from Jiji → Settings. Blank clears the stored value (the config fallback applies).</summary>
public sealed record JijiSettingsInput(
    string? ScriptModel, string? ScriptModelSource, string? SpeechProvider, string? SpeechModel, string? Voice);

public sealed class JijiSettingsService(AppDbContext db, IOptions<JijiOptions> options, IMemoryCache cache,
    DbModelSourceProvider providers, ChatService chat)
{
    private const string CacheKey = "jiji:settings";

    public async Task<JijiEffectiveSettings> GetAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out JijiEffectiveSettings? cached) && cached is not null) return cached;

        var row = await db.JijiSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var opt = options.Value;
        var model = Pick(row?.ScriptModel, opt.Model);
        var effective = new JijiEffectiveSettings(
            model,
            // The source goes with the model it came with: a stored model never pairs with the config's source.
            Pick(row?.ScriptModel) is not null ? Pick(row?.ScriptModelSource) ?? "local" : Pick(opt.ModelSource) ?? "local",
            Pick(row?.SpeechProvider, opt.Speech.Provider),
            Pick(row?.SpeechModel, opt.Speech.Model) ?? "kokoro",
            Pick(row?.Voice, opt.Speech.Voice),
            opt.Speech.Speed is > 0 and <= 4 ? opt.Speech.Speed : 1.0,
            Math.Clamp(opt.Speech.GapMs, 0, 2000));
        cache.Set(CacheKey, effective, TimeSpan.FromMinutes(10));
        return effective;
    }

    /// <summary>SystemAdmin only. Returns the error to show, or null when saved.</summary>
    public async Task<string?> SaveAsync(User user, JijiSettingsInput input, CancellationToken ct)
    {
        if (!AccessPolicy.IsSuperUser(user)) return "Only SystemAdmin can change Jiji's settings.";

        var model = Pick(input.ScriptModel);
        var source = model is null ? null : Pick(input.ScriptModelSource) ?? "local";
        if (model is not null)
        {
            var all = await chat.ListAllModelsAsync(ct);
            if (!all.Any(m => m.Source.Equals(source, StringComparison.OrdinalIgnoreCase) && ModelPolicy.SameModel(m.Name, model)))
                return $"'{model}' is not offered by '{source}' right now.";
        }

        var provider = Pick(input.SpeechProvider);
        var voice = Pick(input.Voice);
        if (provider is not null)
        {
            var rt = providers.Runtime(provider);
            if (rt is null || !rt.Speech) return $"'{provider}' is not a Model providers entry with the speech capability.";
            if (voice is null) return "Pick a voice.";
            var voices = providers.Speech(provider)?.Voices ?? Array.Empty<string>();
            if (voices.Count > 0 && !voices.Contains(voice)) return $"'{voice}' is not in {rt.Name}'s voice list. Test the provider to refresh it.";
        }
        if (voice is { Length: > 100 }) return "The voice id is too long.";
        var speechModel = Pick(input.SpeechModel);
        if (speechModel is { Length: > 100 }) return "The speech model id is too long.";

        var row = await db.JijiSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (row is null) db.JijiSettings.Add(row = new JijiSettings { Id = 1 });
        row.ScriptModel = model;
        row.ScriptModelSource = source;
        row.SpeechProvider = provider;
        row.SpeechModel = speechModel;
        row.Voice = voice;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedByUserId = user.Id;
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey);
        return null;
    }

    private static string? Pick(params string?[] values) =>
        values.Select(v => v?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
