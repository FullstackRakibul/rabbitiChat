using System.Text;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;
using RabbitIC.Api.Rbac;

namespace RabbitIC.Api.Services;

/// <summary>
/// EF-backed <see cref="IContextSourceService"/>.
///
/// Owner scoping is expressed as a filter on every query rather than as a check
/// after the fact, so "not mine" and "not there" are indistinguishable by
/// construction — the caller gets null either way and answers 404, never 403.
/// A 403 would confirm the row exists, which is exactly what we don't want to
/// tell someone probing ids.
/// </summary>
public class ContextSourceService : IContextSourceService
{
    /// <summary>
    /// Keeps one runaway source from eating the whole context window. Trimmed with
    /// a visible marker rather than silently, so the user can tell it happened.
    /// </summary>
    private const int MaxContentCharsPerSource = 8000;

    private readonly AppDbContext _db;
    private readonly AccessPolicy _access;

    public ContextSourceService(AppDbContext db, AccessPolicy access)
    {
        _db = db;
        _access = access;
    }

    public async Task<IReadOnlyList<ContextSourceDto>> ListAsync(
        int userId, string? kind = null, CancellationToken ct = default)
    {
        var q = Owned(userId);
        if (!string.IsNullOrWhiteSpace(kind)) q = q.Where(s => s.Kind == kind);

        return await q
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
            .Select(s => Project(s))
            .ToListAsync(ct);
    }

    public async Task<ContextSourceDto?> GetAsync(int userId, int id, CancellationToken ct = default) =>
        await Owned(userId).Where(s => s.Id == id).Select(s => Project(s)).FirstOrDefaultAsync(ct);

    public async Task<ContextSourceDto> CreateAsync(
        int userId, ContextSourceInput input, CancellationToken ct = default)
    {
        var kind = NormalizeKind(input.Kind);

        // New rows land at the end of their own kind's list.
        var nextOrder = input.SortOrder ?? await NextSortOrderAsync(userId, kind, ct);

        var entity = new ContextSource
        {
            OwnerUserId = userId,
            Name = (input.Name ?? "").Trim(),
            Kind = kind,
            Content = input.Content ?? "",
            IsActive = input.IsActive ?? false,
            SortOrder = nextOrder,
        };

        _db.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Project(entity);
    }

    public async Task<ContextSourceDto?> UpdateAsync(
        int userId, int id, ContextSourceInput input, CancellationToken ct = default)
    {
        var entity = await Owned(userId).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null) return null;

        if (!string.IsNullOrWhiteSpace(input.Name)) entity.Name = input.Name.Trim();
        if (!string.IsNullOrWhiteSpace(input.Kind)) entity.Kind = NormalizeKind(input.Kind);
        if (input.Content is not null) entity.Content = input.Content;
        if (input.IsActive is bool active) entity.IsActive = active;
        if (input.SortOrder is int order) entity.SortOrder = order;

        entity.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync(ct);
        return Project(entity);
    }

    public async Task<bool> DeleteAsync(int userId, int id, CancellationToken ct = default)
    {
        var entity = await Owned(userId).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null) return false;

        _db.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task ReorderAsync(
        int userId, IReadOnlyList<int> orderedIds, CancellationToken ct = default)
    {
        if (orderedIds.Count == 0) return;

        // Load only the caller's rows: ids belonging to someone else simply aren't
        // found here, so a tampered list reorders nothing it shouldn't.
        var mine = await Owned(userId).Where(s => orderedIds.Contains(s.Id)).ToListAsync(ct);
        if (mine.Count == 0) return;

        var position = orderedIds
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);

        var now = DateTime.Now;
        foreach (var row in mine)
        {
            row.SortOrder = position[row.Id];
            row.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<string> BuildSystemPromptAsync(
        int userId, IReadOnlyList<int>? activeIds, string? basePersona,
        string kind = ContextSource.KindSource, CancellationToken ct = default)
    {
        var q = Owned(userId).Where(s => s.Kind == kind);

        // A null activeIds means "trust each row's own switch" (the workspace path).
        // An explicit list is authoritative for this turn, including an empty one,
        // which is how the UI expresses "no context this time".
        q = activeIds is null
            ? q.Where(s => s.IsActive)
            : q.Where(s => activeIds.Contains(s.Id));

        var sources = await q
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
            .Select(s => new { s.Name, s.Content })
            .ToListAsync(ct);

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(basePersona)) sb.AppendLine(basePersona.Trim());

        foreach (var s in sources)
        {
            if (string.IsNullOrWhiteSpace(s.Content)) continue;

            var content = s.Content.Length > MaxContentCharsPerSource
                ? s.Content[..MaxContentCharsPerSource] + "\n…(source truncated)"
                : s.Content;

            if (sb.Length > 0) sb.AppendLine();
            sb.Append("## Source: ").AppendLine(string.IsNullOrWhiteSpace(s.Name) ? "(unnamed)" : s.Name.Trim());
            sb.AppendLine(content.Trim());
        }

        return sb.ToString().TrimEnd();
    }

    // ---- shared kinds ----

    /// <summary>Kinds whose rows are shared rather than owned. Only the HR library today.</summary>
    private static readonly string[] SharedKinds = { ContextSource.KindHr };

    public static bool IsShared(string? kind) => kind is not null && SharedKinds.Contains(kind);

    /// <summary>
    /// Who may change a shared row: whoever holds action:hr.library.edit (seeded
    /// for HrAssistant; SystemAdmin always). Reading is the calling route's
    /// decision (its tab gate); writing is decided here, once.
    /// </summary>
    public Task<bool> CanEditSharedAsync(User user, CancellationToken ct = default) =>
        _access.HasAsync(user, PermissionKeys.HrLibraryEdit, ct);

    public async Task<IReadOnlyList<ContextSource>> ListSharedAsync(string kind, CancellationToken ct = default) =>
        await Shared(kind).OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToListAsync(ct);

    public async Task<ContextSource?> GetSharedAsync(string kind, int id, CancellationToken ct = default) =>
        await Shared(kind).FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<SharedWrite> CreateSharedAsync(User editor, ContextSource item, CancellationToken ct = default)
    {
        if (!IsShared(item.Kind)) throw new ArgumentException($"'{item.Kind}' is not a shared kind.", nameof(item));
        if (!await CanEditSharedAsync(editor, ct)) return SharedWrite.Forbidden;

        var max = await Shared(item.Kind).Select(s => (int?)s.SortOrder).MaxAsync(ct);
        item.OwnerUserId = editor.Id;
        item.SortOrder = (max ?? -1) + 1;
        item.Version = 1;
        item.CreatedAt = item.UpdatedAt = DateTime.Now;

        _db.Add(item);
        await _db.SaveChangesAsync(ct);
        return new SharedWrite(SharedOutcome.Ok, item);
    }

    public async Task<SharedWrite> UpdateSharedAsync(
        User editor, string kind, int id, Action<ContextSource> apply, CancellationToken ct = default)
    {
        var item = await Shared(kind).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (item is null) return SharedWrite.NotFound;
        if (!await CanEditSharedAsync(editor, ct)) return SharedWrite.Forbidden;

        var content = item.Content;
        apply(item);
        item.Kind = kind;   // a shared row stays shared, whatever apply did
        if (!string.Equals(item.Content, content, StringComparison.Ordinal)) item.Version++;
        item.UpdatedAt = DateTime.Now;

        await _db.SaveChangesAsync(ct);
        return new SharedWrite(SharedOutcome.Ok, item);
    }

    public async Task<SharedWrite> DeleteSharedAsync(User editor, string kind, int id, CancellationToken ct = default)
    {
        var item = await Shared(kind).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (item is null) return SharedWrite.NotFound;
        if (!await CanEditSharedAsync(editor, ct)) return SharedWrite.Forbidden;

        _db.Remove(item);
        await _db.SaveChangesAsync(ct);
        return new SharedWrite(SharedOutcome.Ok, item);
    }

    private IQueryable<ContextSource> Shared(string kind) =>
        IsShared(kind)
            ? _db.ContextSources.Where(s => s.Kind == kind)
            : throw new ArgumentException($"'{kind}' is not a shared kind.", nameof(kind));

    /// <summary>
    /// Every owner-scoped read starts here — there is no way to reach another
    /// user's row, and no way to reach a shared row: those have their own
    /// methods above, behind their own route's tab.
    /// </summary>
    private IQueryable<ContextSource> Owned(int userId) =>
        _db.ContextSources.Where(s => s.OwnerUserId == userId && !SharedKinds.Contains(s.Kind));

    private async Task<int> NextSortOrderAsync(int userId, string kind, CancellationToken ct)
    {
        var max = await Owned(userId).Where(s => s.Kind == kind)
            .Select(s => (int?)s.SortOrder).MaxAsync(ct);
        return (max ?? -1) + 1;
    }

    /// <summary>Anything unrecognised falls back to "source" rather than creating a kind nothing lists.</summary>
    private static string NormalizeKind(string? kind)
    {
        var k = (kind ?? "").Trim().ToLowerInvariant();
        return k == ContextSource.KindBuyer ? ContextSource.KindBuyer : ContextSource.KindSource;
    }

    private static ContextSourceDto Project(ContextSource s) =>
        new(s.Id, s.Name, s.Kind, s.Content, s.IsActive, s.SortOrder, s.UpdatedAt);
}
