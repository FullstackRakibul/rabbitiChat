// RBAC — roles and per-tab permissions.
//
// This module sits ON TOP of the existing username+secret auth: a user is still
// a User row with a PBKDF2 secret and a bearer token. All that is added is a
// Role column and an additive grant table. There is no second identity system,
// no claims, and no policy engine.

namespace RabbitIC.Api.Models;

/// <summary>
/// The four roles, as stored in User.Role. A string rather than an int so the
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

    /// <summary>Least to most privileged — the order the admin picker lists them in.</summary>
    public static readonly string[] All = { General, System, GroundControl, SystemAdmin };

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

    public static string Normalize(string? role) =>
        All.FirstOrDefault(r => Matches(r, role)) ?? General;

    public static bool IsKnown(string? role) =>
        All.Any(r => Matches(r, role));

    public static bool IsSystemAdmin(User user) =>
        Normalize(user.Role) == SystemAdmin;

    /// <summary>Display name, e.g. "Ground Control". Unknown input labels as General.</summary>
    public static string LabelFor(string? role) =>
        Normalize(role) == GroundControl ? "Ground Control" : Normalize(role);

    private static bool Matches(string role, string? input)
    {
        var value = input?.Trim();
        if (string.Equals(role, value, StringComparison.OrdinalIgnoreCase)) return true;

        return role == GroundControl &&
            GroundControlSpellings.Contains(value, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// One granted tab for one user. A row means "this user may additionally see
/// this tab"; absence means only that the role baseline decides. There is no
/// deny row in v1 — permissions are purely additive, so a grant can never take
/// something away that the role already gives.
/// </summary>
public class UserTabPermission
{
    public int Id { get; set; }

    public int UserId { get; set; }

    /// <summary>A stable tab id from <see cref="RabbitIC.Api.Rbac.TabCatalog"/> — e.g. "workspace".</summary>
    public string TabKey { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User? User { get; set; }
}
