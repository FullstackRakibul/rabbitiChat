// Access → Models: reading and saving the model rules of a role or a user, the module defaults,
// and the first-run seed. Rules are decided in ModelAccess; this file only stores them.

using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;
using RabbitIC.Api.Rbac;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.ModelProviders;

/// <summary>One grant as the editor sends it.</summary>
public sealed record ModelGrantDto(string Module, string ProviderKey, string ModelId, double? MaxMultiplier);

/// <summary>A role's or a user's rules: grants, Locked per module, and the monthly cap (weighted tokens; null = none).</summary>
public sealed record ModelRulesDto(IReadOnlyList<ModelGrantDto> Grants, Dictionary<string, bool> Locked, long? Cap);

public sealed record ModelUserRulesDto(
    int UserId, string Username, string Role, ModelRulesDto User, ModelRulesDto RoleRules,
    long? EffectiveCap, double UsedThisMonth);

public sealed record ModelCatalogModelDto(string Name, double? Multiplier);
public sealed record ModelCatalogSourceDto(string Key, string Label, bool Paid, IReadOnlyList<ModelCatalogModelDto> Models);
public sealed record ModelDefaultDto(string Source, string Model);
public sealed record ModelAccessCatalogDto(IReadOnlyList<ModelCatalogSourceDto> Sources, Dictionary<string, ModelDefaultDto> Defaults, IReadOnlyList<string> Roles);

public sealed class ModelAccessAdmin(AppDbContext db, ModelAccess access, ChatService chat, DbModelSourceProvider providers)
{
    public async Task<ModelAccessCatalogDto> CatalogAsync(CancellationToken ct)
    {
        var all = await chat.ListAllModelsAsync(ct);
        var sources = all
            .Where(m => !ModelSources.ModelNaming.IsEmbeddingModel(m.Name))
            .GroupBy(m => m.Source, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ModelCatalogSourceDto(g.Key, SourceLabel(g.Key), access.IsPaid(g.Key),
                g.Select(m => new ModelCatalogModelDto(m.Name, m.Multiplier)).ToList()))
            .OrderBy(s => s.Key == "local" ? 0 : s.Key == "cloud" ? 1 : 2).ThenBy(s => s.Label)
            .ToList();
        var defaults = new Dictionary<string, ModelDefaultDto>();
        foreach (var module in ModelModules.All)
            if (await access.DefaultAsync(module, ct) is { } d) defaults[module] = new(d.Source, d.Model);
        return new(sources, defaults, UserRoles.All);
    }

    private string SourceLabel(string key) => key switch
    {
        "local" => "Local (Ollama)",
        "cloud" => "Ollama Cloud",
        _ => providers.Runtime(key)?.Name ?? key,
    };

    public async Task<ModelRulesDto> RoleAsync(string role, CancellationToken ct) =>
        ToDto((await access.RulesAsync(ct)).Where(r => r.UserId is null && SameRole(r.Role, role)));

    public async Task<ModelUserRulesDto?> UserAsync(int userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return null;
        var rules = await access.RulesAsync(ct);
        var roleRows = rules.Where(r => r.UserId is null && SameRole(r.Role, user.Role)).ToList();
        var userRows = rules.Where(r => r.UserId == userId).ToList();
        var cap = AccessPolicy.IsSuperUser(user) ? null : ModelAccess.EffectiveCap(roleRows, userRows);
        return new(user.Id, user.Username, UserRoles.Normalize(user.Role), ToDto(userRows, forUser: true), ToDto(roleRows), cap,
            Math.Round(await access.UsedThisMonthAsync(userId, ct), 1));
    }

    public Task<string?> SaveRoleAsync(string role, ModelRulesDto input, int byUserId, CancellationToken ct)
    {
        var normalized = UserRoles.Normalize(role);
        if (!UserRoles.All.Contains(normalized)) return Task.FromResult<string?>($"Unknown role '{role}'.");
        return SaveAsync(r => r.UserId == null && r.Role == normalized, () => new ModelAccessRule { Role = normalized }, input, isUser: false, byUserId, ct);
    }

    public async Task<string?> SaveUserAsync(int userId, ModelRulesDto input, int byUserId, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct)) return "No such user.";
        return await SaveAsync(r => r.UserId == userId, () => new ModelAccessRule { UserId = userId }, input, isUser: true, byUserId, ct);
    }

    /// <summary>Replaces the subject's rows. A user gets settings rows only where they add (an unlock, a cap).</summary>
    private async Task<string?> SaveAsync(System.Linq.Expressions.Expression<Func<ModelAccessRule, bool>> subject,
        Func<ModelAccessRule> make, ModelRulesDto input, bool isUser, int byUserId, CancellationToken ct)
    {
        var grants = (input.Grants ?? Array.Empty<ModelGrantDto>())
            .Select(g => g with { Module = g.Module?.Trim().ToLowerInvariant() ?? "", ProviderKey = g.ProviderKey?.Trim() ?? "", ModelId = g.ModelId?.Trim() ?? "" })
            .ToList();
        if (grants.Any(g => !ModelModules.IsKnown(g.Module) || g.ProviderKey.Length == 0 || g.ModelId.Length == 0 || g.ModelId.Length > 200))
            return "Every model needs a module (chat, hr, jiji), a provider and a model id.";
        if (grants.Any(g => g.MaxMultiplier is < 0))
            return "A multiplier ceiling cannot be negative.";
        if (input.Cap is < 0) return "The monthly cap cannot be negative.";
        var locked = input.Locked ?? new Dictionary<string, bool>();

        var rows = new List<ModelAccessRule>();
        ModelAccessRule Row(string module, string provider, string model, double? max, bool isLocked)
        {
            var r = make();
            r.Module = module; r.ProviderKey = provider; r.ModelId = model; r.MaxMultiplier = max;
            r.Locked = isLocked; r.MonthlyWeightedTokenCap = input.Cap; r.UpdatedByUserId = byUserId; r.UpdatedAt = DateTime.UtcNow;
            return r;
        }

        foreach (var module in ModelModules.All)
        {
            var isLocked = locked.TryGetValue(module, out var l) && l;
            // A user row that is not locked unlocks the module for them, so a user's grant rows
            // carry Locked = true unless the editor explicitly unlocked the module.
            var userUnlocks = isUser && locked.TryGetValue(module, out var ul) && !ul;
            var rowLocked = isUser ? !userUnlocks : isLocked;
            foreach (var g in grants.Where(g => g.Module == module).DistinctBy(g => (g.ProviderKey, g.ModelId)))
                rows.Add(Row(module, g.ProviderKey, g.ModelId, g.MaxMultiplier, rowLocked));
            if (!isUser || userUnlocks) rows.Add(Row(module, "", "", null, rowLocked));
        }
        if (!isUser || input.Cap is not null) rows.Add(Row(ModelModules.Any, "", "", null, false));

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ModelAccessRules.RemoveRange(await db.ModelAccessRules.Where(subject).ToListAsync(ct));
        db.ModelAccessRules.AddRange(rows);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        access.Invalidate();
        return null;
    }

    public async Task<string?> SetDefaultAsync(string module, ModelDefaultDto input, int byUserId, CancellationToken ct)
    {
        if (!ModelModules.IsKnown(module)) return $"Unknown module '{module}'.";
        if (string.IsNullOrWhiteSpace(input.Model)) return "Pick a model.";
        var all = await chat.ListAllModelsAsync(ct);
        if (!all.Any(m => m.Source.Equals(input.Source, StringComparison.OrdinalIgnoreCase) && ModelPolicy.SameModel(m.Name, input.Model)))
            return $"'{input.Model}' is not listed by '{input.Source}' right now.";

        var row = await db.ModelModuleDefaults.FirstOrDefaultAsync(d => d.Module == module, ct);
        if (row is null) db.ModelModuleDefaults.Add(row = new ModelModuleDefault { Module = module });
        row.ProviderKey = input.Source.Trim();
        row.ModelId = input.Model.Trim();
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedByUserId = byUserId;
        await db.SaveChangesAsync(ct);
        access.Invalidate();
        return null;
    }

    private static bool SameRole(string? a, string? b) => UserRoles.Normalize(a) == UserRoles.Normalize(b);

    /// <summary>For a user, Locked false means "unlocked for this user"; true means "as the role has it".</summary>
    private static ModelRulesDto ToDto(IEnumerable<ModelAccessRule> rows, bool forUser = false)
    {
        var list = rows.ToList();
        return new ModelRulesDto(
            list.Where(r => r.ProviderKey.Length > 0).Select(r => new ModelGrantDto(r.Module, r.ProviderKey, r.ModelId, r.MaxMultiplier)).ToList(),
            ModelModules.All.ToDictionary(m => m, m => forUser
                ? !list.Any(r => r.Module == m && !r.Locked)
                : list.Any(r => r.Module == m && r.Locked)),
            list.Max(r => r.MonthlyWeightedTokenCap));
    }

    // ---- First run ----

    /// <summary>
    /// The role defaults, once, when the table is empty. Each role keeps the Ollama models its
    /// existing model policy gives it in Chat (so nobody's Chat stops working), and gets paid
    /// models as follows: System ≤ 0.5x, GroundControl ≤ 1.3x, SystemAdmin everything; HR is
    /// locked to the HR default for every role but SystemAdmin.
    /// </summary>
    public static void Seed(AppDbContext db, IConfiguration config, ILogger log)
    {
        if (!db.ModelModuleDefaults.Any(d => d.Module == ModelModules.Hr))
        {
            db.ModelModuleDefaults.Add(new ModelModuleDefault
            {
                Module = ModelModules.Hr, ProviderKey = "local", ModelId = config["Hr:Model"] ?? "gemma4:12b",
            });
            db.SaveChanges();
        }
        if (db.ModelAccessRules.Any()) return;

        var policies = db.RoleModelPolicies.AsNoTracking().ToList()
            .ToDictionary(p => UserRoles.Normalize(p.Role), p => ModelPolicy.Split(p.AllowedModels));
        var caps = new Dictionary<string, long?>
        {
            [UserRoles.General] = 20_000, [UserRoles.System] = 100_000, [UserRoles.GroundControl] = 200_000, [UserRoles.SystemAdmin] = null,
        };
        var paidCeiling = new Dictionary<string, double>
        {
            [UserRoles.System] = 0.5, [UserRoles.GroundControl] = 1.3,
        };

        var rows = new List<ModelAccessRule>();
        foreach (var role in UserRoles.All)
        {
            var cap = caps.TryGetValue(role, out var c) ? c : 20_000;
            ModelAccessRule Row(string module, string provider, string model, double? max, bool locked) => new()
            {
                Role = role, Module = module, ProviderKey = provider, ModelId = model, MaxMultiplier = max,
                Locked = locked, MonthlyWeightedTokenCap = cap,
            };

            if (role == UserRoles.SystemAdmin)
            {
                foreach (var module in ModelModules.All)
                {
                    rows.Add(Row(module, "*", "*", null, false));
                    rows.Add(Row(module, "local", "*", null, false));
                    rows.Add(Row(module, "cloud", "*", null, false));
                    rows.Add(Row(module, "", "", null, false));
                }
            }
            else
            {
                // Chat: the role's Ollama models as its model policy has them today; one model = locked, as it showed before.
                var local = policies.TryGetValue(role, out var l) ? l : new List<string>();
                var chatLocked = local.Count == 1 && !paidCeiling.ContainsKey(role);
                foreach (var m in local) rows.Add(Row(ModelModules.Chat, "local", m, null, chatLocked));
                if (paidCeiling.TryGetValue(role, out var ceiling)) rows.Add(Row(ModelModules.Chat, "*", "*", ceiling, false));
                rows.Add(Row(ModelModules.Chat, "", "", null, chatLocked));
                // HR: locked to the HR default.
                rows.Add(Row(ModelModules.Hr, "", "", null, true));
                rows.Add(Row(ModelModules.Jiji, "", "", null, false));
            }
            rows.Add(Row(ModelModules.Any, "", "", null, false));
        }
        db.ModelAccessRules.AddRange(rows);
        db.SaveChanges();
        log.LogInformation("Model access: seeded {Count} role rules.", rows.Count);
    }
}
