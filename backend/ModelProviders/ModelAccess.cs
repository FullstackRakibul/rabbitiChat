// Which models a user may run in Chat, HR and Jiji — THE rule for those three modules, in one place.
//
// /api/models?module= lists through it and every chat, HR and Jiji call checks through it, so a
// picker never offers what the server would refuse, and a model named straight to the API is
// refused the same way (403, never a silent switch). Paid models (Settings providers) count
// weighted tokens against a monthly cap (429); Ollama is free: allow list only, no cap.
//
// SystemAdmin is unrestricted: every listed model, nothing locked, never capped.
// Other modules (Matrix, PO Studio, …) keep Rbac/ModelPolicy.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RabbitIC.Api.Data;
using RabbitIC.Api.ModelSources;
using RabbitIC.Api.Models;
using RabbitIC.Api.Rbac;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.ModelProviders;

/// <summary>Allowed with the model to use, or refused with the status (400, 403, 429) and the sentence to show.</summary>
public sealed record ModelAccessDecision(bool Allowed, int Status, string? Error, string? Source, string? Model)
{
    public static ModelAccessDecision Ok(string source, string model) => new(true, 200, null, source, model);
    public static ModelAccessDecision Refuse(int status, string error) => new(false, status, error, null, null);
}

/// <summary>What one user gets in one module.</summary>
public sealed record ModuleAccess(
    bool Unrestricted, IReadOnlyList<ModelAccessRule> Grants, bool Locked, long? Cap, (string Source, string Model)? Default);

public sealed class ModelAccess(AppDbContext db, IMemoryCache cache, ChatService chat, DbModelSourceProvider providers)
{
    public const string BudgetReached = "Monthly model budget reached";
    private const string RulesKey = "model-access:rules";
    private const string DefaultsKey = "model-access:defaults";

    // ---- What a user gets ----

    public async Task<ModuleAccess> ForAsync(User user, string module, CancellationToken ct = default)
    {
        var def = await DefaultAsync(module, ct);
        if (AccessPolicy.IsSuperUser(user)) return new ModuleAccess(true, Array.Empty<ModelAccessRule>(), false, null, def);

        var role = UserRoles.Normalize(user.Role);
        var rules = await RulesAsync(ct);
        var roleRows = rules.Where(r => r.UserId is null && r.Role is not null && UserRoles.Normalize(r.Role) == role).ToList();
        var userRows = rules.Where(r => r.UserId == user.Id).ToList();

        var grants = roleRows.Concat(userRows)
            .Where(r => r.Module == module && r.ProviderKey.Length > 0).ToList();
        // A user's rows only add: one of theirs in this module that is not locked unlocks it.
        var locked = roleRows.Any(r => r.Module == module && r.Locked)
                     && !userRows.Any(r => r.Module == module && !r.Locked);
        return new ModuleAccess(false, grants, locked, EffectiveCap(roleRows, userRows), def);
    }

    /// <summary>A role with no cap is uncapped; a user's own cap can only raise their role's.</summary>
    public static long? EffectiveCap(IEnumerable<ModelAccessRule> roleRows, IEnumerable<ModelAccessRule> userRows)
    {
        var roleCap = roleRows.Max(r => r.MonthlyWeightedTokenCap);
        if (roleCap is null) return null;
        var userCap = userRows.Max(r => r.MonthlyWeightedTokenCap);
        return userCap is null ? roleCap : Math.Max(roleCap.Value, userCap.Value);
    }

    /// <summary>The models of <paramref name="all"/> this user may run in the module, and whether it is locked.</summary>
    public async Task<(IReadOnlyList<ModelInfo> Models, bool Locked)> ListAsync(
        User user, string module, IReadOnlyList<ModelInfo> all, CancellationToken ct = default)
    {
        var access = await ForAsync(user, module, ct);
        if (access.Unrestricted) return (all, false);
        if (access.Locked)
        {
            var locked = LockedModel(access, all);
            return (locked is null ? Array.Empty<ModelInfo>() : new[] { locked }, true);
        }
        return (all.Where(m => access.Grants.Any(g => Matches(g, m))).ToList(), false);
    }

    /// <summary>
    /// May this user run this model here? Call it before anything is stored or streamed. A blank model
    /// means "the module's default" (a locked user's only model). Never substitutes another model.
    /// </summary>
    public async Task<ModelAccessDecision> CheckAsync(
        User user, string module, string? source, string? model, CancellationToken ct = default)
    {
        var access = await ForAsync(user, module, ct);
        var all = await chat.ListAllModelsAsync(ct);
        var label = ModelModules.Label(module);

        if (string.IsNullOrWhiteSpace(model))
        {
            var pick = access.Locked ? LockedModel(access, all) is { } l ? (l.Source, l.Name) : ((string, string)?)null : access.Default;
            if (pick is null) return ModelAccessDecision.Refuse(400, $"Pick a model for {label}.");
            (source, model) = pick.Value;
        }
        source = string.IsNullOrWhiteSpace(source) ? "local" : source.Trim();
        model = model.Trim();

        var listed = all.FirstOrDefault(m => m.Source.Equals(source, StringComparison.OrdinalIgnoreCase) && ModelPolicy.SameModel(m.Name, model));
        var paid = IsPaid(source);

        if (access.Unrestricted)
        {
            // A Settings provider serves only the models ticked for it, admin or not.
            if (paid && listed is null)
                return ModelAccessDecision.Refuse(403, $"'{model}' is not one of {ProviderName(source)}'s allowed models. Tick it in Settings → Model providers.");
            return ModelAccessDecision.Ok(source, model);
        }

        if (access.Locked)
        {
            var only = LockedModel(access, all);
            if (only is null)
                return ModelAccessDecision.Refuse(403, $"{label} has no model set for your role. Ask an administrator.");
            if (!only.Source.Equals(source, StringComparison.OrdinalIgnoreCase) || !ModelPolicy.SameModel(only.Name, model))
                return ModelAccessDecision.Refuse(403, $"Your role uses {only.Name} in {label}; '{model}' is not available to you.");
        }
        else if (listed is null || !access.Grants.Any(g => Matches(g, listed)))
        {
            var allowed = all.Where(m => access.Grants.Any(g => Matches(g, m))).Select(m => m.Name).Take(8).ToList();
            return ModelAccessDecision.Refuse(403, allowed.Count == 0
                ? $"No model is available to your role in {label}."
                : $"'{model}' is not available to your role in {label}. You can use: {string.Join(", ", allowed)}.");
        }

        if (paid && access.Cap is { } cap)
        {
            var used = await UsedThisMonthAsync(user.Id, ct);
            if (used >= cap)
                return ModelAccessDecision.Refuse(429,
                    $"{BudgetReached}: {used:N0} of {cap:N0} weighted tokens used this month. It resets on the 1st (Asia/Dhaka).");
        }
        return ModelAccessDecision.Ok(source, model);
    }

    // ---- Matching ----

    /// <summary>A grant covers a model: same source (or "*" for any Settings provider), same id (or "*"), under the multiplier ceiling.</summary>
    public bool Matches(ModelAccessRule g, ModelInfo m)
    {
        var paid = IsPaid(m.Source);
        var providerOk = g.ProviderKey == "*" ? paid : g.ProviderKey.Equals(m.Source, StringComparison.OrdinalIgnoreCase);
        if (!providerOk) return false;
        if (g.ModelId != "*" && !ModelPolicy.SameModel(g.ModelId, m.Name)) return false;
        if (g.MaxMultiplier is { } max && paid && (m.Multiplier ?? DbModelSourceProvider.DefaultMultiplier) > max + 1e-9) return false;
        return true;
    }

    /// <summary>The one model of a locked module: the module's default, else the first granted model.</summary>
    private ModelInfo? LockedModel(ModuleAccess access, IReadOnlyList<ModelInfo> all)
    {
        if (access.Default is { } d)
        {
            // Shown even while its source is unreachable: the lock is a fact, not a listing.
            return all.FirstOrDefault(m => m.Source.Equals(d.Source, StringComparison.OrdinalIgnoreCase) && ModelPolicy.SameModel(m.Name, d.Model))
                   ?? new ModelInfo(d.Model, d.Source, null, new[] { "chat" }, IsPaid(d.Source));
        }
        return all.FirstOrDefault(m => access.Grants.Any(g => Matches(g, m)));
    }

    /// <summary>A Settings provider: metered, counted and capped. Ollama (local and cloud) is not.</summary>
    public bool IsPaid(string source) => providers.Runtime(source) is not null;

    private string ProviderName(string source) => providers.Runtime(source)?.Name ?? source;

    // ---- Budget ----

    private static readonly TimeZoneInfo Dhaka = FindDhaka();

    private static TimeZoneInfo FindDhaka()
    {
        foreach (var id in new[] { "Asia/Dhaka", "Bangladesh Standard Time" })
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        return TimeZoneInfo.CreateCustomTimeZone("Asia/Dhaka", TimeSpan.FromHours(6), "Asia/Dhaka", "Asia/Dhaka");
    }

    /// <summary>The 1st of this month at 00:00 in Asia/Dhaka, as UTC. Caps and the Settings counter reset here.</summary>
    public static DateTime MonthStartUtc(DateTime? utcNow = null)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow ?? DateTime.UtcNow, Dhaka);
        var start = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(start, Dhaka);
    }

    public async Task<double> UsedThisMonthAsync(int userId, CancellationToken ct = default)
    {
        var start = MonthStartUtc();
        return await db.ModelProviderUsages.AsNoTracking()
            .Where(u => u.UserId == userId && u.CreatedAt >= start)
            .SumAsync(u => u.WeightedTokens, ct);
    }

    // ---- Rows, cached; Invalidate on every save ----

    public async Task<IReadOnlyList<ModelAccessRule>> RulesAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(RulesKey, out IReadOnlyList<ModelAccessRule>? cached) && cached is not null) return cached;
        var rows = await db.ModelAccessRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct);
        cache.Set(RulesKey, (IReadOnlyList<ModelAccessRule>)rows);
        return rows;
    }

    public async Task<(string Source, string Model)?> DefaultAsync(string module, CancellationToken ct = default)
    {
        if (!cache.TryGetValue(DefaultsKey, out Dictionary<string, (string, string)>? map) || map is null)
        {
            map = (await db.ModelModuleDefaults.AsNoTracking().ToListAsync(ct))
                .Where(d => d.ModelId.Length > 0)
                .ToDictionary(d => d.Module, d => (d.ProviderKey, d.ModelId), StringComparer.OrdinalIgnoreCase);
            cache.Set(DefaultsKey, map);
        }
        return map.TryGetValue(module, out var d) ? d : null;
    }

    public void Invalidate()
    {
        cache.Remove(RulesKey);
        cache.Remove(DefaultsKey);
    }
}
