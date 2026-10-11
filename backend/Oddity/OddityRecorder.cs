// Oddity — the request log: write one row per model call, and read them back
// owner-scoped with keyset paging on Id (the same paging the activity log uses).

using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.DTOs.Oddity;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Oddity;

public sealed class OddityRecorder
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly AppDbContext _db;

    public OddityRecorder(AppDbContext db) => _db = db;

    /// <summary>Writes with CancellationToken.None: the row must land even when the caller has gone.</summary>
    public async Task WriteAsync(OddityRequest row)
    {
        _db.OddityRequests.Add(row);
        await _db.SaveChangesAsync(CancellationToken.None);
    }

    public async Task<OddityRequestPageDto> ListAsync(int ownerId, OddityRequestFilter f, CancellationToken ct = default)
    {
        var limit = f.Limit is > 0 and <= MaxLimit ? f.Limit.Value : DefaultLimit;
        var q = _db.OddityRequests.AsNoTracking().Where(r => r.OwnerUserId == ownerId);

        if (f.Before is { } before) q = q.Where(r => r.Id < before);
        if (f.KeyId is { } keyId) q = q.Where(r => r.KeyId == keyId);
        if (!string.IsNullOrWhiteSpace(f.Profile)) q = q.Where(r => r.Profile == f.Profile);
        if (!string.IsNullOrWhiteSpace(f.Model)) q = q.Where(r => r.Model == f.Model);
        if (!string.IsNullOrWhiteSpace(f.Ip)) q = q.Where(r => r.ClientIp == f.Ip);
        if (f.From is { } from) q = q.Where(r => r.TimestampUtc >= from);
        if (f.To is { } to) q = q.Where(r => r.TimestampUtc <= to);

        // "status" is either an HTTP status code or an outcome name.
        if (!string.IsNullOrWhiteSpace(f.Status))
        {
            q = int.TryParse(f.Status, out var code)
                ? q.Where(r => r.StatusCode == code)
                : q.Where(r => r.Outcome == f.Status);
        }

        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            var term = f.Q.Trim();
            q = q.Where(r =>
                (r.CorrelationId != null && r.CorrelationId.Contains(term)) ||
                r.KeyName.Contains(term) ||
                (r.Model != null && r.Model.Contains(term)) ||
                (r.Error != null && r.Error.Contains(term)));
        }

        var page = await q.OrderByDescending(r => r.Id).Take(limit + 1).ToListAsync(ct);
        var more = page.Count > limit;
        if (more) page.RemoveAt(page.Count - 1);

        return new OddityRequestPageDto(page.Select(ToDto).ToList(), more ? page[^1].Id : null);
    }

    /// <summary>Null when the id is absent or someone else's.</summary>
    public async Task<OddityRequestDetailDto?> GetAsync(int ownerId, long id, CancellationToken ct = default)
    {
        var r = await _db.OddityRequests.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.OwnerUserId == ownerId, ct);
        return r is null ? null : new OddityRequestDetailDto(ToDto(r), r.ReceiptJson, r.RequestPreview, r.ResponsePreview);
    }

    private static OddityRequestDto ToDto(OddityRequest r) => new(
        r.Id, r.TimestampUtc, r.CorrelationId, r.KeyId, r.KeyName, r.ClientIp, r.UserAgent, r.Endpoint,
        r.RequestedModel, r.Model, r.Profile, r.Stream, r.StatusCode, r.Outcome, r.Error, r.DoneReason,
        r.PromptTokens, r.CachedPromptTokens, r.OutputTokens, r.LoadMs, r.PromptMs, r.GenerationMs,
        r.FirstByteMs, r.TotalMs, r.EstimatedPromptTokens, r.AddedTokens, r.RequestBytes, r.ResponseBytes);
}
