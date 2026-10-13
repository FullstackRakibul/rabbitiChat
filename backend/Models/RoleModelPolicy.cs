namespace RabbitIC.Api.Models;

/// <summary>
/// Which models one role may run. Held in the database rather than code so a
/// SystemAdmin can change it without a redeploy.
///
/// One row per role. An empty <see cref="AllowedModels"/> means no restriction,
/// which is both the historical behaviour and the behaviour for every role but
/// General — so a missing row can never accidentally lock anyone out.
/// </summary>
public class RoleModelPolicy
{
    /// <summary>A canonical role name (UserRoles.All). Primary key.</summary>
    public string Role { get; set; } = "";

    /// <summary>
    /// Comma-separated model names. Empty means every local model, as today.
    /// Compared case-insensitively, and a bare name matches the same name with
    /// ":latest" — see ModelPolicy.
    /// </summary>
    public string AllowedModels { get; set; } = "";

    /// <summary>
    /// The model to use when a request names none. Must be one of
    /// <see cref="AllowedModels"/> whenever that list is not empty.
    /// </summary>
    public string DefaultModel { get; set; } = "";

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public int? UpdatedByUserId { get; set; }
}
