using RabbitIC.Api.Models;

namespace RabbitIC.Api.Services;

/// <summary>Create/update payload. Null members mean "leave unchanged" on update.</summary>
public record ContextSourceInput(string? Name, string? Kind, string? Content, bool? IsActive, int? SortOrder);

/// <summary>What the API and the UI see. Content is included so the editor can round-trip it.</summary>
public record ContextSourceDto(
    int Id, string Name, string Kind, string Content, bool IsActive, int SortOrder, DateTime UpdatedAt);

/// <summary>
/// Owner-scoped CRUD over <see cref="ContextSource"/> plus the one operation that
/// matters to chat: assembling the system prompt.
///
/// Consumers (the chat endpoint, the workspace endpoint, the HTTP surface) depend
/// on this interface rather than on the DbContext, so the assembly rule cannot
/// drift between the two tabs — there is only one implementation of it.
/// </summary>
public interface IContextSourceService
{
    /// <summary>The user's sources, ordered by SortOrder then Id. <paramref name="kind"/> null = every kind.</summary>
    Task<IReadOnlyList<ContextSourceDto>> ListAsync(int userId, string? kind = null, CancellationToken ct = default);

    /// <summary>Null when the id doesn't exist <em>or</em> belongs to someone else — callers map both to 404.</summary>
    Task<ContextSourceDto?> GetAsync(int userId, int id, CancellationToken ct = default);

    Task<ContextSourceDto> CreateAsync(int userId, ContextSourceInput input, CancellationToken ct = default);

    /// <summary>Null when the id is absent or foreign.</summary>
    Task<ContextSourceDto?> UpdateAsync(int userId, int id, ContextSourceInput input, CancellationToken ct = default);

    /// <summary>False when the id is absent or foreign.</summary>
    Task<bool> DeleteAsync(int userId, int id, CancellationToken ct = default);

    /// <summary>
    /// Applies a new ordering. Ids that aren't the user's are ignored rather than
    /// erroring, so a stale client list can't corrupt someone else's rows.
    /// </summary>
    Task ReorderAsync(int userId, IReadOnlyList<int> orderedIds, CancellationToken ct = default);

    /// <summary>
    /// The single place a system prompt is assembled: the base persona followed by
    /// each active source's content, delimited and in SortOrder.
    /// </summary>
    /// <param name="activeIds">
    /// Ids the caller wants active for this turn. Null means "use each source's own
    /// IsActive flag" — that is what the stateless workspace surface passes.
    /// </param>
    Task<string> BuildSystemPromptAsync(
        int userId, IReadOnlyList<int>? activeIds, string? basePersona,
        string kind = ContextSource.KindSource, CancellationToken ct = default);
}
