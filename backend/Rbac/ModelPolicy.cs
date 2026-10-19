// Which models a role may run. THE rule, in one place.
//
// Every endpoint that runs a model calls Check() here. A copy of the comparison
// in an endpoint would be a second rule that drifts, and the point of a policy
// is that there is exactly one answer to "may this user run this model".
//
// Two things are easy to get wrong and are handled deliberately:
//
//   · The model to check is the RESOLVED one, not the one the caller typed.
//     Extraction falls back request → buyer profile → install default, so a
//     General user who names nothing can still end up pointed at a 27B by a
//     profile. Check the result of the fallback, never the input.
//
//   · The source matters as much as the name. A metered cloud model can share a
//     local model's name, so a restricted role is confined to `local` — a name
//     match alone would be a way around the policy.
//
// Rows are cached because this runs on every model request; the cache is dropped
// on save so a change applies to the next request without a restart.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Rbac;

/// <summary>The outcome of a policy check: allowed, or refused with the reason to show.</summary>
public sealed record ModelPolicyResult(bool Allowed, string? Error)
{
    public static readonly ModelPolicyResult Ok = new(true, null);
    public static ModelPolicyResult Refused(string error) => new(false, error);
}

public sealed class ModelPolicy(AppDbContext db, IMemoryCache cache)
{
    /// <summary>The only source a restricted role may reach (§3.1).</summary>
    public const string LocalSource = "local";

    private const string CacheKey = "rbac:model-policies";

    /// <summary>
    /// The role's allowed models, or an empty list meaning "no restriction".
    /// SystemAdmin is never restricted, whatever a row says.
    /// </summary>
    public async Task<IReadOnlyList<string>> AllowedAsync(User user, CancellationToken ct = default)
    {
        // SystemAdmin is never restricted — AccessPolicy's one rule for it.
        if (AccessPolicy.IsSuperUser(user)) return Array.Empty<string>();
        var role = UserRoles.Normalize(user.Role);

        var rows = await RowsAsync(ct);
        return rows.TryGetValue(role, out var row) ? Split(row.AllowedModels) : Array.Empty<string>();
    }

    /// <summary>True when the role is confined to exactly one model — the UI locks its picker.</summary>
    public async Task<bool> IsLockedAsync(User user, CancellationToken ct = default)
        => (await AllowedAsync(user, ct)).Count == 1;

    /// <summary>
    /// The model to use when a request names none, or null when the role is
    /// unrestricted and the caller should fall back as it always has.
    /// </summary>
    public async Task<string?> DefaultForAsync(User user, CancellationToken ct = default)
    {
        if (AccessPolicy.IsSuperUser(user)) return null;
        var role = UserRoles.Normalize(user.Role);

        var rows = await RowsAsync(ct);
        if (!rows.TryGetValue(role, out var row)) return null;
        if (Split(row.AllowedModels).Count == 0) return null;
        return string.IsNullOrWhiteSpace(row.DefaultModel) ? null : row.DefaultModel.Trim();
    }

    /// <summary>
    /// May this user run this model from this source? Call it BEFORE the model
    /// call and before anything is written, so a refusal leaves no trace.
    /// </summary>
    /// <param name="source">
    /// The model source key ("local", "cloud", …). Null is treated as local,
    /// which is what the callers that have no source concept mean.
    /// </param>
    /// <param name="context">
    /// Prepended to the refusal when the model was chosen for the user rather
    /// than by them — e.g. "This buyer profile uses qwen3.8-27b-q3." Without it
    /// a refusal for a profile's model reads as though the user had asked for it.
    /// </param>
    public async Task<ModelPolicyResult> CheckAsync(
        User user, string? model, string? source = null, string? context = null,
        CancellationToken ct = default)
    {
        var allowed = await AllowedAsync(user, ct);
        if (allowed.Count == 0) return ModelPolicyResult.Ok;   // unrestricted role

        var role = UserRoles.LabelFor(user.Role);
        var list = string.Join(", ", allowed);
        var prefix = string.IsNullOrWhiteSpace(context) ? "" : context!.TrimEnd() + " ";

        // A restricted role may only reach local models: a cloud model could
        // otherwise be named past the policy, metered and all.
        if (!string.IsNullOrWhiteSpace(source)
            && !source!.Equals(LocalSource, StringComparison.OrdinalIgnoreCase))
            return ModelPolicyResult.Refused(
                $"{prefix}Your role ({role}) can use: {list}. " +
                $"Models from the '{source}' source are not available to your role.");

        if (string.IsNullOrWhiteSpace(model))
            return ModelPolicyResult.Refused($"{prefix}Your role ({role}) can use: {list}.");

        if (allowed.Any(a => SameModel(a, model!))) return ModelPolicyResult.Ok;

        return ModelPolicyResult.Refused($"{prefix}Your role ({role}) can use: {list}.");
    }

    /// <summary>
    /// The models of <paramref name="models"/> this user may run. Used by
    /// /api/models so a picker offers nothing the server would refuse.
    /// </summary>
    public async Task<IReadOnlyList<T>> FilterAsync<T>(
        User user, IEnumerable<T> models, Func<T, (string Name, string Source)> describe,
        CancellationToken ct = default)
    {
        var allowed = await AllowedAsync(user, ct);
        var all = models.ToList();
        if (allowed.Count == 0) return all;

        return all.Where(m =>
        {
            var (name, source) = describe(m);
            if (!string.IsNullOrWhiteSpace(source)
                && !source.Equals(LocalSource, StringComparison.OrdinalIgnoreCase))
                return false;
            return allowed.Any(a => SameModel(a, name));
        }).ToList();
    }

    // ---- rows and cache ----

    /// <summary>Every policy row by canonical role name. Cached; see <see cref="Invalidate"/>.</summary>
    public async Task<IReadOnlyDictionary<string, RoleModelPolicy>> RowsAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyDictionary<string, RoleModelPolicy>? cached)
            && cached is not null)
            return cached;

        var rows = await db.RoleModelPolicies.AsNoTracking().ToListAsync(ct);
        var map = rows
            .GroupBy(r => UserRoles.Normalize(r.Role))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        cache.Set(CacheKey, (IReadOnlyDictionary<string, RoleModelPolicy>)map);
        return map;
    }

    /// <summary>Drop the cache, so the next request reads the saved rows.</summary>
    public void Invalidate() => cache.Remove(CacheKey);

    // ---- naming ----

    /// <summary>"a, b" → ["a", "b"], dropping blanks.</summary>
    public static List<string> Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? new List<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>
    /// Model names compare case-insensitively, and a bare name equals the same
    /// name tagged ":latest" — the two spellings are the same model to Ollama,
    /// so treating them as different would refuse a model the admin did allow.
    /// </summary>
    public static bool SameModel(string a, string b) =>
        Canonical(a).Equals(Canonical(b), StringComparison.OrdinalIgnoreCase);

    private static string Canonical(string name)
    {
        var trimmed = (name ?? "").Trim();
        return trimmed.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^":latest".Length]
            : trimmed;
    }
}
