using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Rbac;

/// <summary>What the policy decided, and why.</summary>
public enum ModelPolicyOutcome
{
    /// <summary>Run it.</summary>
    Allowed,

    /// <summary>The role may not run this model. <see cref="ModelPolicyResult.Message"/> says what it may.</summary>
    Refused,
}

/// <summary>
/// One decision. <see cref="Message"/> is written for the person who hit it, so
/// every endpoint can hand it straight back — plain text or JSON — without
/// restating the rule and drifting from it.
/// </summary>
public record ModelPolicyResult(ModelPolicyOutcome Outcome, string Message)
{
    public bool IsAllowed => Outcome == ModelPolicyOutcome.Allowed;

    public static readonly ModelPolicyResult Allow = new(ModelPolicyOutcome.Allowed, "");
    public static ModelPolicyResult Refuse(string message) => new(ModelPolicyOutcome.Refused, message);
}

/// <summary>
/// The model policy — §3.1, written once and called from every endpoint that
/// runs a model.
///
/// There is no second copy of this rule anywhere, and deliberately so: a check
/// repeated at each call site is a check that will eventually be forgotten at
/// one of them, and the forgotten one is the hole.
/// </summary>
public interface IModelPolicy
{
    /// <summary>The model names this user may run. Empty means no restriction.</summary>
    Task<IReadOnlyList<string>> AllowedAsync(User user, CancellationToken ct = default);

    /// <summary>True when the user is held to a single model, which the UI shows locked.</summary>
    Task<bool> IsLockedAsync(User user, CancellationToken ct = default);

    /// <summary>
    /// The gate. Call before any model call and before anything is saved.
    ///
    /// <paramref name="source"/> matters as much as the name: a restricted role is
    /// confined to local models, so a metered cloud model cannot be reached by
    /// naming it.
    /// </summary>
    Task<ModelPolicyResult> CheckAsync(
        User user, string? model, string? source, CancellationToken ct = default);

    /// <summary>
    /// The model to use when a request names none. Null when the role is
    /// unrestricted, so the caller keeps its existing config fallback.
    /// </summary>
    Task<string?> DefaultForAsync(User user, CancellationToken ct = default);

    /// <summary>Drops the cache so a saved change applies to the next request, with no restart.</summary>
    void Invalidate();
}

/// <summary>
/// EF-backed <see cref="IModelPolicy"/> with a process-wide cache.
///
/// The rows change only when an admin saves one, and every model call reads
/// them, so they are cached until <see cref="Invalidate"/> is called. The cache
/// is static because the service is scoped per request — a per-instance cache
/// would be a cache that never hits.
/// </summary>
public class ModelPolicy : IModelPolicy
{
    /// <summary>Only local models are reachable by a restricted role (§3.1).</summary>
    private const string LocalSource = "local";

    private static readonly object Gate = new();
    private static Dictionary<string, RoleModelPolicy>? _cache;

    private readonly AppDbContext _db;

    public ModelPolicy(AppDbContext db) => _db = db;

    public void Invalidate()
    {
        lock (Gate) _cache = null;
    }

    /// <summary>Clears the cache from anywhere that writes a row without this service.</summary>
    public static void InvalidateAll()
    {
        lock (Gate) _cache = null;
    }

    public async Task<IReadOnlyList<string>> AllowedAsync(User user, CancellationToken ct = default)
    {
        var row = await RowForAsync(user, ct);
        return Parse(row?.AllowedModels);
    }

    public async Task<bool> IsLockedAsync(User user, CancellationToken ct = default) =>
        (await AllowedAsync(user, ct)).Count == 1;

    public async Task<ModelPolicyResult> CheckAsync(
        User user, string? model, string? source, CancellationToken ct = default)
    {
        var allowed = await AllowedAsync(user, ct);

        // No row, or an empty list: today's behaviour, unchanged.
        if (allowed.Count == 0) return ModelPolicyResult.Allow;

        var role = UserRoles.Normalize(user.Role);

        // A restricted role is confined to local models. Checked before the name,
        // so a cloud model that happens to share an allowed name is still refused.
        if (!string.IsNullOrWhiteSpace(source)
            && !string.Equals(source, LocalSource, StringComparison.OrdinalIgnoreCase))
        {
            return ModelPolicyResult.Refuse(Message(role, allowed));
        }

        if (!string.IsNullOrWhiteSpace(model) && allowed.Any(a => SameModel(a, model)))
            return ModelPolicyResult.Allow;

        return ModelPolicyResult.Refuse(Message(role, allowed));
    }

    public async Task<string?> DefaultForAsync(User user, CancellationToken ct = default)
    {
        var row = await RowForAsync(user, ct);
        if (row is null) return null;

        var allowed = Parse(row.AllowedModels);
        if (allowed.Count == 0) return null;   // unrestricted: keep the config chain

        var chosen = row.DefaultModel?.Trim();
        return string.IsNullOrEmpty(chosen) ? allowed[0] : chosen;
    }

    /// <summary>
    /// "Your role (General) can use: qwen3.5:0.8b." — names the role so the reader
    /// knows why, and the models so they know what to pick instead.
    /// </summary>
    public static string Message(string role, IReadOnlyList<string> allowed) =>
        $"Your role ({role}) can use: {string.Join(", ", allowed)}.";

    /// <summary>
    /// Case-insensitive, and a bare name equals the same name tagged ":latest" —
    /// Ollama treats "llama3" and "llama3:latest" as one model, so the policy must
    /// too, or an untagged request would slip past a tagged allow-list.
    /// </summary>
    public static bool SameModel(string? a, string? b)
    {
        static string Canon(string? s)
        {
            var t = (s ?? "").Trim();
            if (t.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)) t = t[..^7];
            return t.ToLowerInvariant();
        }

        return Canon(a).Length > 0 && Canon(a) == Canon(b);
    }

    /// <summary>Splits the stored list. Blank entries are dropped rather than stored as "".</summary>
    public static IReadOnlyList<string> Parse(string? csv) =>
        (csv ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private async Task<RoleModelPolicy?> RowForAsync(User user, CancellationToken ct)
    {
        var role = UserRoles.Normalize(user.Role);

        // SystemAdmin is never restricted (§3.3): an admin who locked themselves
        // out of every model could not reach the screen that would undo it.
        if (role == UserRoles.SystemAdmin) return null;

        var rows = await LoadAsync(ct);
        return rows.TryGetValue(role, out var row) ? row : null;
    }

    private async Task<Dictionary<string, RoleModelPolicy>> LoadAsync(CancellationToken ct)
    {
        lock (Gate)
        {
            if (_cache is not null) return _cache;
        }

        var rows = await _db.RoleModelPolicies.AsNoTracking().ToListAsync(ct);
        var map = rows.ToDictionary(r => UserRoles.Normalize(r.Role), StringComparer.OrdinalIgnoreCase);

        lock (Gate) _cache = map;
        return map;
    }
}
