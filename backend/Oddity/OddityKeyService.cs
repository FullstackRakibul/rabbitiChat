// Oddity — gateway keys.
//
// Format: "odd_" + 8 lowercase hex (the public prefix) + "_" + 43 base64url
// characters (32 random bytes). Only the prefix and a SHA-256 of the whole key
// are stored. SHA-256 rather than PBKDF2 because the key is 32 random bytes,
// not a password: there is nothing for a slow hash to protect against.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.DTOs.Oddity;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Oddity;

public sealed partial class OddityKeyService
{
    private readonly AppDbContext _db;

    public OddityKeyService(AppDbContext db) => _db = db;

    // ---- The gateway side ----

    /// <summary>
    /// The key in an "Authorization: Bearer odd_…" header, if it exists, is enabled
    /// and has not expired; otherwise null. Callers answer every null the same way,
    /// so a caller cannot tell an unknown key from a revoked one.
    /// </summary>
    public async Task<OddityKey?> VerifyAsync(string? authorization, CancellationToken ct = default)
    {
        const string bearer = "Bearer ";
        if (authorization is null || !authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)) return null;

        var presented = authorization[bearer.Length..].Trim();
        var match = KeyFormat().Match(presented);
        if (!match.Success) return null;

        var prefix = match.Groups[1].Value;
        var row = await _db.OddityKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Prefix == prefix, ct);

        // Hash even when the prefix is unknown, so a miss costs the same as a hit.
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        if (row is null) return null;

        var storedHash = Convert.FromHexString(row.KeyHash);
        if (!CryptographicOperations.FixedTimeEquals(storedHash, presentedHash)) return null;
        if (!row.Enabled) return null;
        if (row.ExpiresAtUtc is { } exp && exp <= DateTime.UtcNow) return null;

        return row;
    }

    // ---- The admin side ----

    public async Task<(OddityKeyCreatedDto? Created, string? Error)> CreateAsync(
        int ownerId, OddityKeyInput input, CancellationToken ct = default)
    {
        var error = Validate(input);
        if (error is not null) return (null, error);

        // A clash on 32 bits is rare but possible; the unique index would refuse it.
        string prefix;
        do prefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        while (await _db.OddityKeys.AnyAsync(k => k.Prefix == prefix, ct));

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var key = $"odd_{prefix}_{secret}";

        var row = new OddityKey
        {
            OwnerUserId = ownerId,
            Name = input.Name!.Trim(),
            Note = NullIfBlank(input.Note),
            Prefix = prefix,
            KeyHash = Hash(key),
            RatePerMinute = input.RatePerMinute,
            Enabled = input.Enabled ?? true,
            ExpiresAtUtc = ToUtc(input.ExpiresAtUtc),
        };
        _db.OddityKeys.Add(row);
        await _db.SaveChangesAsync(ct);

        return (new OddityKeyCreatedDto(
            row.Id, row.Name, row.Note, row.Prefix, row.RatePerMinute, row.Enabled,
            row.ExpiresAtUtc, row.CreatedAtUtc, key), null);
    }

    /// <summary>The owner's keys, newest first, with "last used" and the 24 h count read from the request log.</summary>
    public async Task<List<OddityKeyDto>> ListAsync(int ownerId, CancellationToken ct = default)
    {
        var keys = await _db.OddityKeys.AsNoTracking()
            .Where(k => k.OwnerUserId == ownerId)
            .OrderByDescending(k => k.Id)
            .ToListAsync(ct);

        // Read from OddityRequests rather than a column written per call: every
        // write to the key row would otherwise be audited.
        var ids = keys.Select(k => k.Id).ToList();
        var since = DateTime.UtcNow.AddHours(-24);
        var usage = await _db.OddityRequests.AsNoTracking()
            .Where(r => r.OwnerUserId == ownerId && ids.Contains(r.KeyId))
            .GroupBy(r => r.KeyId)
            .Select(g => new
            {
                KeyId = g.Key,
                Last = g.Max(r => r.TimestampUtc),
                Recent = g.Count(r => r.TimestampUtc >= since),
            })
            .ToDictionaryAsync(x => x.KeyId, ct);

        return keys.Select(k =>
        {
            usage.TryGetValue(k.Id, out var u);
            return new OddityKeyDto(
                k.Id, k.Name, k.Note, k.Prefix, k.DefaultProfileId, k.RatePerMinute, k.Enabled,
                k.ExpiresAtUtc, k.CreatedAtUtc,
                u is null ? null : DateTime.SpecifyKind(u.Last, DateTimeKind.Utc),
                u?.Recent ?? 0);
        }).ToList();
    }

    /// <summary>Null when the id is absent or someone else's — both read as 404.</summary>
    public async Task<(OddityKey? Row, string? Error)> UpdateAsync(
        int ownerId, int id, OddityKeyInput input, CancellationToken ct = default)
    {
        var row = await _db.OddityKeys.FirstOrDefaultAsync(k => k.Id == id && k.OwnerUserId == ownerId, ct);
        if (row is null) return (null, null);

        var error = Validate(input);
        if (error is not null) return (row, error);

        row.Name = input.Name!.Trim();
        row.Note = NullIfBlank(input.Note);
        row.RatePerMinute = input.RatePerMinute;
        row.Enabled = input.Enabled ?? row.Enabled;
        row.ExpiresAtUtc = ToUtc(input.ExpiresAtUtc);
        await _db.SaveChangesAsync(ct);
        return (row, null);
    }

    /// <summary>False when the id is absent or someone else's. Request rows stay.</summary>
    public async Task<bool> DeleteAsync(int ownerId, int id, CancellationToken ct = default)
    {
        var row = await _db.OddityKeys.FirstOrDefaultAsync(k => k.Id == id && k.OwnerUserId == ownerId, ct);
        if (row is null) return false;
        _db.OddityKeys.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public static OddityKeyDto ToDto(OddityKey k) => new(
        k.Id, k.Name, k.Note, k.Prefix, k.DefaultProfileId, k.RatePerMinute, k.Enabled,
        k.ExpiresAtUtc, k.CreatedAtUtc, null, 0);

    // ---- Helpers ----

    private static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private static string? Validate(OddityKeyInput i)
    {
        if (string.IsNullOrWhiteSpace(i.Name)) return "A key needs a name.";
        if (i.Name.Trim().Length > 80) return "The name can be at most 80 characters.";
        if ((i.Note?.Length ?? 0) > 500) return "The note can be at most 500 characters.";
        if (i.RatePerMinute is < 0 or > 100_000) return "The rate must be between 0 (no limit) and 100000 a minute.";
        return null;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static DateTime? ToUtc(DateTime? d) => d switch
    {
        null => null,
        { Kind: DateTimeKind.Local } v => v.ToUniversalTime(),
        { } v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
    };

    [GeneratedRegex(@"^odd_([0-9a-f]{8})_([A-Za-z0-9_-]{43})$")]
    private static partial Regex KeyFormat();
}
