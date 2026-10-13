using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Rbac;

/// <summary>
/// Brings the RBAC columns to a usable state at startup: every row carries a
/// known role, and the built-in administrator exists.
///
/// Both entry points are idempotent and run on every boot, so a fresh database
/// and a long-lived one converge on the same state. Nothing here reads or
/// rewrites user content — only the Role column and, at most, one new account.
///
/// The seed secret is never written to a log. It reaches only
/// <see cref="Authorization.HashSecret"/>, which is the same PBKDF2 helper
/// /auth/login verifies against; there is no second hashing path to drift out
/// of step with it.
/// </summary>
public static class RbacSeeder
{
    /// <summary>The built-in administrator. Its role is enforced; its secret is not.</summary>
    private const string AdminUsername = "mostisko@admin";

    /// <summary>
    /// Only ever applied when the account is created. An existing admin keeps
    /// whatever secret it has — see <see cref="Run"/>.
    /// </summary>
    private const string AdminSeedSecret = "q12wQ!@W";

    /// <summary>
    /// Gives every row a role the rest of the system recognises.
    ///
    /// The SQL default already writes 'General' for rows that existed before the
    /// column, so this exists for the cases the default cannot cover: a row
    /// written with an empty string, or with a value that is no longer a known
    /// role. <see cref="UserRoles.Normalize"/> decides, which means anything
    /// unrecognised fails closed to General rather than to something privileged.
    /// </summary>
    public static void NormalizeExistingRoles(AppDbContext db)
    {
        var changed = 0;

        foreach (var user in db.Users)
        {
            var normalized = UserRoles.Normalize(user.Role);
            if (user.Role == normalized) continue;

            user.Role = normalized;
            changed++;
        }

        if (changed > 0) db.SaveChanges();
    }

    /// <summary>
    /// Ensures the built-in administrator exists and holds SystemAdmin.
    ///
    /// Two distinct cases, deliberately treated differently:
    ///   absent  — create it with the seed secret, hashed through the shared helper.
    ///   present — promote it to SystemAdmin if needed, and leave the secret alone.
    ///
    /// That second case matters: an operator who has changed the admin password
    /// would otherwise find it silently reset to a published default on the next
    /// restart, which is the opposite of securing the account.
    /// </summary>
    public static void Run(AppDbContext db, ILogger logger)
    {
        var admin = db.Users.FirstOrDefault(u => u.Username == AdminUsername);

        if (admin is null)
        {
            // Same helpers /auth/login uses, so the seeded credential verifies
            // through exactly the code path a normal login takes.
            var (hash, salt) = Authorization.HashSecret(AdminSeedSecret);

            admin = new User
            {
                Username = AdminUsername,
                SecretHash = hash,
                SecretSalt = salt,
                Token = Authorization.NewToken(),
                Role = UserRoles.SystemAdmin,
            };

            db.Users.Add(admin);
            db.SaveChanges();

            // Username and role only — the secret never reaches the log.
            logger.LogInformation(
                "RBAC: seeded administrator {Username} as {Role}.", AdminUsername, UserRoles.SystemAdmin);
            return;
        }

        if (UserRoles.Normalize(admin.Role) != UserRoles.SystemAdmin)
        {
            var previous = admin.Role;
            admin.Role = UserRoles.SystemAdmin;
            db.SaveChanges();

            logger.LogInformation(
                "RBAC: promoted {Username} from {Previous} to {Role}; its secret was left unchanged.",
                AdminUsername, UserRoles.Normalize(previous), UserRoles.SystemAdmin);
        }
    }

    /// <summary>
    /// One model-policy row per role (§3.2 of docs/MODEL_POLICY_PROMPT.md).
    ///
    /// Only for roles that have no row yet, so a role added in a later build gets
    /// its row on the next start and an administrator's edit is NEVER overwritten
    /// — the same reasoning as the admin secret above: a restart must not undo a
    /// deliberate change.
    ///
    /// General starts confined to the install's default extraction model; every
    /// other role starts unrestricted, which is both today's behaviour and the
    /// safe default for a missing row.
    /// </summary>
    public static void SeedModelPolicies(AppDbContext db, IConfiguration config, ILogger logger)
    {
        var generalDefault =
            Blank(config["Extraction:DefaultModel"])
            ?? Blank(config["Ollama:ExtractionModel"])
            ?? "gemma4:12b";

        var existing = db.RoleModelPolicies.Select(p => p.Role).ToList();
        var added = new List<string>();

        foreach (var role in UserRoles.All)
        {
            if (existing.Any(r => UserRoles.Normalize(r) == role)) continue;

            var restricted = role == UserRoles.General;
            db.RoleModelPolicies.Add(new RoleModelPolicy
            {
                Role = role,
                AllowedModels = restricted ? generalDefault : "",
                DefaultModel = restricted ? generalDefault : "",
                UpdatedAtUtc = DateTime.UtcNow,
            });
            added.Add(role);
        }

        if (added.Count == 0) return;

        db.SaveChanges();
        logger.LogInformation(
            "RBAC: seeded model policies for {Roles} (General limited to {Model}).",
            string.Join(", ", added), generalDefault);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
