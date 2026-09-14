// RBAC — roles and per-tab permissions.
//
// This module sits ON TOP of the existing username+secret auth: a user is still
// a User row with a PBKDF2 secret and a bearer token. All that is added is a
// Role column and an additive grant table. There is no second identity system,
// no claims, and no policy engine.

namespace RabbitIC.Api.Models;

/// <summary>
/// The three roles, as stored in User.Role. A string rather than an int so the
/// column is readable in SQLite and an unknown future value degrades to
/// "General" instead of silently becoming a different role.
/// </summary>
public static class UserRoles
{
    public const string General = "General";
    public const string System = "System";
    public const string SystemAdmin = "SystemAdmin";

    public static readonly string[] All = { General, System, SystemAdmin };

    /// <summary>
    /// Maps arbitrary input to a known role. Anything unrecognised — including a
    /// null column on a row written before this feature — reads as General, the
    /// least-privileged role. Failing closed is the point.
    /// </summary>
    public static string Normalize(string? role) =>
        All.FirstOrDefault(r => string.Equals(r, role?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? General;

    public static bool IsKnown(string? role) =>
        All.Any(r => string.Equals(r, role?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool IsSystemAdmin(User user) =>
        Normalize(user.Role) == SystemAdmin;
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
