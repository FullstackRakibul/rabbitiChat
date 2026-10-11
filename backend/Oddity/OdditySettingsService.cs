// Oddity — the install-wide settings row.
//
// Read on every gateway call, so it is cached in memory and the cache is dropped
// on save: a change applies to the very next request, with no restart.

using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.DTOs.Oddity;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Oddity;

public sealed partial class OdditySettingsService
{
    public const string LogOff = "off";
    public const string LogPreview = "preview";
    public const string LogFull = "full";

    /// <summary>Process-wide: there is one settings row, whichever request reads it.</summary>
    private static OdditySettings? _cache;

    private readonly AppDbContext _db;

    public OdditySettingsService(AppDbContext db) => _db = db;

    /// <summary>The settings row, created with defaults the first time. Treat it as read-only.</summary>
    public async Task<OdditySettings> GetAsync(CancellationToken ct = default)
    {
        var cached = Volatile.Read(ref _cache);
        if (cached is not null) return cached;

        var row = await _db.OdditySettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new OdditySettings();
            _db.OdditySettings.Add(row);
            await _db.SaveChangesAsync(ct);
            _db.Entry(row).State = EntityState.Detached;
        }

        Volatile.Write(ref _cache, row);
        return row;
    }

    /// <summary>Validates and saves. On failure returns the errors keyed by field, and nothing is written.</summary>
    public async Task<(OdditySettingsDto? Saved, Dictionary<string, string> Errors)> SaveAsync(
        OdditySettingsInput input, int userId, CancellationToken ct = default)
    {
        var errors = Validate(input);
        if (errors.Count > 0) return (null, errors);

        var current = await GetAsync(ct);
        var row = await _db.OdditySettings.FirstAsync(s => s.Id == current.Id, ct);

        row.Enabled = input.Enabled;
        row.DefaultModel = input.DefaultModel?.Trim() ?? "";
        row.AllowedModels = string.Join(",", CleanModels(input.AllowedModels));
        row.NumCtx = input.NumCtx;
        row.ReplyReserve = input.ReplyReserve;
        row.KeepAlive = input.KeepAlive!.Trim();
        row.DefaultRatePerMinute = input.DefaultRatePerMinute;
        row.MaxInlineFileMb = input.MaxInlineFileMb;
        row.LogBodies = input.LogBodies!.Trim().ToLowerInvariant();
        row.RetentionDays = input.RetentionDays;
        row.PublicBaseUrl = NormalizePublicUrl(input.PublicBaseUrl) ?? "";
        row.UpdatedAtUtc = DateTime.UtcNow;
        row.UpdatedByUserId = userId;

        await _db.SaveChangesAsync(ct);
        Volatile.Write(ref _cache, null);

        return (ToDto(row), errors);
    }

    public static OdditySettingsDto ToDto(OdditySettings s) => new(
        s.Enabled, s.DefaultModel, AllowedList(s), s.NumCtx, s.ReplyReserve, s.KeepAlive,
        s.DefaultRatePerMinute, s.MaxInlineFileMb, s.LogBodies, s.RetentionDays,
        s.UpdatedAtUtc, s.UpdatedByUserId, s.PublicBaseUrl);

    /// <summary>
    /// "" for blank, the normalised address for a valid one (lowercase host, no
    /// trailing slash), or null when it is not an absolute http(s) address without
    /// a query, fragment or credentials.
    /// </summary>
    public static string? NormalizePublicUrl(string? value)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0) return "";
        if (text.Length > 300) return null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0) return null;
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    public static List<string> AllowedList(OdditySettings s) =>
        s.AllowedModels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>
    /// Whether a model may be called. An empty list allows any model. Names compare
    /// case-insensitively and ":latest" is ignored, as Ollama itself does.
    /// </summary>
    public static bool IsAllowed(OdditySettings s, string? model)
    {
        var allowed = AllowedList(s);
        if (allowed.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(model)) return false;

        var want = BaseName(model);
        return allowed.Any(a => string.Equals(BaseName(a), want, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"qwen3:latest" and "qwen3" name the same model.</summary>
    public static string BaseName(string model)
    {
        var m = model.Trim();
        return m.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? m[..^":latest".Length] : m;
    }

    private static Dictionary<string, string> Validate(OdditySettingsInput i)
    {
        var e = new Dictionary<string, string>();

        if (i.NumCtx is < 2048 or > 262144)
            e["numCtx"] = "The window must be between 2048 and 262144 tokens.";
        if (i.ReplyReserve < 0 || i.ReplyReserve > i.NumCtx / 2)
            e["replyReserve"] = "The reply reserve must be between 0 and half the window.";
        if (string.IsNullOrWhiteSpace(i.KeepAlive) || !KeepAlivePattern().IsMatch(i.KeepAlive.Trim()))
            e["keepAlive"] = "Use a number of seconds (-1 keeps the model loaded) or a duration such as 30m or 2h.";
        if (i.DefaultRatePerMinute is < 0 or > 100_000)
            e["defaultRatePerMinute"] = "The rate must be between 0 (no limit) and 100000 a minute.";
        if (i.MaxInlineFileMb is < 1 or > 20)
            e["maxInlineFileMb"] = "The file size limit must be between 1 and 20 MB.";
        if (i.LogBodies?.Trim().ToLowerInvariant() is not (LogOff or LogPreview or LogFull))
            e["logBodies"] = "Body logging must be off, preview or full.";
        if (i.RetentionDays is < 1 or > 3650)
            e["retentionDays"] = "Retention must be between 1 and 3650 days.";
        if ((i.DefaultModel?.Length ?? 0) > 200)
            e["defaultModel"] = "The default model name is too long.";
        if ((i.AllowedModels ?? new()).Any(m => m.Contains(',')))
            e["allowedModels"] = "A model name cannot contain a comma.";
        if (NormalizePublicUrl(i.PublicBaseUrl) is null)
            e["publicBaseUrl"] = "The public gateway URL must be a full http(s) address, such as https://ai.example.com/oddity, with no query.";

        return e;
    }

    private static IEnumerable<string> CleanModels(List<string>? models) =>
        (models ?? new())
            .Select(m => m.Trim())
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>A whole number of seconds, or Ollama's Go-style duration ("30m", "1h30m", "90s").</summary>
    [GeneratedRegex(@"^(-?\d+|(\d+(\.\d+)?(ns|us|µs|ms|s|m|h))+)$")]
    private static partial Regex KeepAlivePattern();
}
