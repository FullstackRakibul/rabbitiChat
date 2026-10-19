// RBAC — roles and per-tab permissions.
//
// This module sits ON TOP of the existing username+secret auth: a user is still
// a User row with a PBKDF2 secret and a bearer token. All that is added is a
// Role column and an additive grant table. There is no second identity system,
// no claims, and no policy engine.

namespace RabbitIC.Api.Models;

/// <summary>
/// The five roles, as stored in User.Role. A string rather than an int so the
/// column is readable in SQLite and an unknown future value degrades to
/// "General" instead of silently becoming a different role.
/// </summary>
public static class UserRoles
{
    public const string General = "General";
    public const string System = "System";
    /// <summary>Operates the Oddity gateway: System's tabs plus "oddity".</summary>
    public const string GroundControl = "GroundControl";
    public const string SystemAdmin = "SystemAdmin";

    /// <summary>
    /// HR staff using the মানব সম্পদ module (docs/HR_MODULE_PROMPT.md §3.1): its
    /// own tabs and the HR library, and nothing model-backed beyond them. Shown
    /// as "hr-assistant". Narrow rather than higher than General, so it sits next
    /// to it in the picker.
    /// </summary>
    public const string HrAssistant = "HrAssistant";

    /// <summary>
    /// The people who upload stored procedures to the মানব সম্পদ Procedures section
    /// and answer what their code values mean (docs/HR_PROCEDURE_ANALYZER_PROMPT.md
    /// §3.7): the HR tab (Procedures only) and Settings. Narrow, like HrAssistant.
    /// </summary>
    public const string DevOps = "DevOps";

    /// <summary>Least to most privileged — the order the admin picker lists them in.</summary>
    public static readonly string[] All = { General, HrAssistant, DevOps, System, GroundControl, SystemAdmin };

    /// <summary>
    /// Maps arbitrary input to a known role. Anything unrecognised — including a
    /// null column on a row written before this feature — reads as General, the
    /// least-privileged role. Failing closed is the point.
    /// </summary>
    /// <summary>
    /// The other ways "GroundControl" may be written on input. Deliberately a short
    /// list for this one role: every other role still has to match exactly, so a
    /// loose spelling can never land on a more privileged role.
    /// </summary>
    private static readonly string[] GroundControlSpellings =
    {
        "ground-control", "ground control", "ground_control",
    };

    /// <summary>The same allowance for HrAssistant, whose display name is "hr-assistant".</summary>
    private static readonly string[] HrAssistantSpellings =
    {
        "hr-assistant", "hr assistant", "hr_assistant",
    };

    public static string Normalize(string? role) =>
        All.FirstOrDefault(r => Matches(r, role)) ?? General;

    public static bool IsKnown(string? role) =>
        All.Any(r => Matches(r, role));

    /// <summary>Display name, e.g. "Ground Control". Unknown input labels as General.</summary>
    public static string LabelFor(string? role) => Normalize(role) switch
    {
        GroundControl => "Ground Control",
        HrAssistant => "hr-assistant",
        var r => r,
    };

    private static bool Matches(string role, string? input)
    {
        var value = input?.Trim();
        if (string.Equals(role, value, StringComparison.OrdinalIgnoreCase)) return true;

        return role switch
        {
            GroundControl => GroundControlSpellings.Contains(value, StringComparer.OrdinalIgnoreCase),
            HrAssistant => HrAssistantSpellings.Contains(value, StringComparer.OrdinalIgnoreCase),
            _ => false,
        };
    }
}

/// <summary>
/// What one role gets for one permission key (Rbac/PermissionCatalog.cs). One row
/// per role and key, seeded from today's baselines for missing rows only, so an
/// administrator's edit survives every restart.
/// </summary>
public class RolePermission
{
    /// <summary>A canonical role name (<see cref="UserRoles.All"/>).</summary>
    public string Role { get; set; } = "";

    /// <summary>A catalog key, e.g. "tab:chat" or "action:hr.rulebook.confirm".</summary>
    public string Key { get; set; } = "";

    public bool Allowed { get; set; }

    /// <summary>Null for a seeded row.</summary>
    public int? UpdatedByUserId { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One user's Allow or Deny on one key, over their role's value. A tab grant
/// from before role presets is an "allow" row here.
/// </summary>
public class UserPermissionOverride
{
    public const string Allow = "allow";
    public const string Deny = "deny";

    public int UserId { get; set; }

    public string Key { get; set; } = "";

    /// <summary><see cref="Allow"/> or <see cref="Deny"/>.</summary>
    public string Effect { get; set; } = Allow;

    public int? UpdatedByUserId { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public string? Note { get; set; }
}

/// <summary>
/// One change to access: who, what, before → after, when, and why. Append-only —
/// SQLite triggers refuse UPDATE and DELETE (SchemaUpgrade.EnsureAccessSchema).
/// </summary>
public class AccessChangeLogEntry
{
    public const string ScopeRole = "role";
    public const string ScopeUser = "user";

    public long Id { get; set; }

    public DateTime AtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Null when the system made the change (the grant migration).</summary>
    public int? ByUserId { get; set; }

    /// <summary><see cref="ScopeRole"/> or <see cref="ScopeUser"/>.</summary>
    public string Scope { get; set; } = ScopeRole;

    /// <summary>The role name, or the user's id.</summary>
    public string Target { get; set; } = "";

    /// <summary>A catalog key, or "role" for a user's role change.</summary>
    public string Key { get; set; } = "";

    public string? Before { get; set; }

    public string? After { get; set; }

    public string? Note { get; set; }
}
