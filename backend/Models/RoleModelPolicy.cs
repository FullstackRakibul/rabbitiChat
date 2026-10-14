namespace RabbitIC.Api.Models;

/// <summary>
/// Which models one role may run.
///
/// A row per role rather than per user: the policy is a property of the tier, so
/// promoting someone lifts their limit at once and no per-user bookkeeping drifts
/// out of step with it.
///
/// An EMPTY <see cref="AllowedModels"/> means "no restriction" — today's
/// behaviour, and what every role but General is seeded with. That asymmetry is
/// deliberate: absence of a rule has to mean freedom, so a role added later
/// without a row is never silently locked out of every model.
/// </summary>
public class RoleModelPolicy
{
    /// <summary>Canonical role name — the primary key. See <see cref="UserRoles"/>.</summary>
    public string Role { get; set; } = "";

    /// <summary>
    /// Comma-separated model names. Empty means unrestricted.
    ///
    /// Stored as one string rather than a child table because it is read and
    /// written whole, never queried across, and a role has a handful of models.
    /// </summary>
    public string AllowedModels { get; set; } = "";

    /// <summary>
    /// The model used when a request names none (only PO extraction allows that).
    /// Must be one of <see cref="AllowedModels"/> when that list is non-empty;
    /// empty falls back to the existing config chain.
    /// </summary>
    public string DefaultModel { get; set; } = "";

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Who last saved it. 0 for a row written by the seeder.</summary>
    public int UpdatedByUserId { get; set; }
}
