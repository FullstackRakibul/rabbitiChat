// সত্য মন্ত্রণালয় — the staff side (docs/TRUTH_PROMPT.md §2, §8) and what the
// public endpoints read.
//
// Staff edit the Tr* tables; a person publishes an issue, which runs the
// pre-publish checklist and, when it is clean, writes a snapshot. The public
// reads snapshots only, and only of issues that are still published — so a
// draft, an unpublished edit or a withdrawn issue is never served (G1.2).
//
// Every rule is decided here; TruthApis translates, the Vue tab only shows.

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.DTOs.Truth;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Truth;

public enum TrOutcome { Ok, NotFound, Invalid, Conflict }

/// <summary>One thing that blocks publishing. <see cref="ItemId"/> null: the issue itself.</summary>
public sealed record TrProblem(int? ItemId, string Code, string Message, string? Field = null);

public sealed record TrResult<T>(TrOutcome Outcome, T? Value = default, string? Error = null, List<TrProblem>? Problems = null)
{
    public static TrResult<T> Ok(T value) => new(TrOutcome.Ok, value);
    public static TrResult<T> NotFound(string error) => new(TrOutcome.NotFound, default, error);
    public static TrResult<T> Invalid(string error, List<TrProblem>? problems = null) => new(TrOutcome.Invalid, default, error, problems);
    public static TrResult<T> Conflict(string error) => new(TrOutcome.Conflict, default, error);
}

/// <summary>One published snapshot as the public endpoints serve it.</summary>
public sealed record PublicSnapshot(string Json, string ETag, bool Urgent);

public sealed partial class TruthService(AppDbContext db)
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex Slug();

    [GeneratedRegex(@"^CVE-\d{4}-\d{4,7}$")]
    private static partial Regex CveId();

    [GeneratedRegex(@"^[\p{L}\p{N}]{1,2}$")]
    private static partial Regex Monogram();

    [GeneratedRegex(@"\[(\d{1,2})\]")]
    private static partial Regex Citation();

    /// <summary>Rule 7: a private, loopback or link-local IPv4 address in public text is our own infrastructure.</summary>
    [GeneratedRegex(@"\b(10\.\d{1,3}|127\.\d{1,3}|192\.168|169\.254|172\.(1[6-9]|2\d|3[01]))\.\d{1,3}\.\d{1,3}\b")]
    private static partial Regex PrivateIp();

    // ─────────────────────────── technologies ───────────────────────────

    public async Task<List<TrTech>> ListTechsAsync(CancellationToken ct) =>
        await db.TrTechs.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);

    public async Task<TrResult<TrTech>> SaveTechAsync(int? id, TechInput input, CancellationToken ct)
    {
        var slug = input.Slug?.Trim().ToLowerInvariant() ?? "";
        if (!Slug().IsMatch(slug)) return TrResult<TrTech>.Invalid("The slug is lower-case letters, digits and dashes (up to 40).");
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 60) return TrResult<TrTech>.Invalid("The name is required (up to 60 characters).");
        if (!TrTech.Categories.Contains(input.Category)) return TrResult<TrTech>.Invalid($"The category is one of: {string.Join(", ", TrTech.Categories)}.");
        if (!Monogram().IsMatch(input.Monogram?.Trim() ?? "")) return TrResult<TrTech>.Invalid("The lettermark is one or two letters or digits.");
        if (input.Hue is < 0 or > 359) return TrResult<TrTech>.Invalid("The hue is 0–359.");
        if (!string.IsNullOrWhiteSpace(input.Homepage) && !IsHttps(input.Homepage)) return TrResult<TrTech>.Invalid("The homepage must be an https link.");
        if (await db.TrTechs.AnyAsync(t => t.Slug == slug && t.Id != (id ?? 0), ct)) return TrResult<TrTech>.Conflict($"A technology with the slug {slug} exists.");

        TrTech tech;
        if (id is null) db.TrTechs.Add(tech = new TrTech());
        else if ((tech = (await db.TrTechs.FindAsync(new object[] { id.Value }, ct))!) is null) return TrResult<TrTech>.NotFound("No such technology.");
        tech.Slug = slug;
        tech.Name = input.Name.Trim();
        tech.Category = input.Category;
        tech.Monogram = input.Monogram!.Trim().ToUpperInvariant();
        tech.Hue = input.Hue;
        tech.Homepage = string.IsNullOrWhiteSpace(input.Homepage) ? null : input.Homepage.Trim();
        tech.VersionLinesJson = TruthJson.FromStrings(input.VersionLines);
        await db.SaveChangesAsync(ct);
        return TrResult<TrTech>.Ok(tech);
    }

    // ─────────────────────────── issues ───────────────────────────

    public async Task<List<object>> ListIssuesAsync(CancellationToken ct)
    {
        var issues = await db.TrIssues.AsNoTracking().OrderByDescending(i => i.WeekStart).ThenByDescending(i => i.Id).ToListAsync(ct);
        var counts = await db.TrItems.AsNoTracking().GroupBy(i => i.IssueId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var names = await UserNamesAsync(issues.Select(i => i.PublishedByUserId ?? 0), ct);
        return issues.Select(i => (object)new
        {
            i.Id, i.Slug, i.TitleBn, i.TitleEn, i.WeekStart, i.WeekEnd, i.Status,
            publishedAt = TruthJson.Utc(i.PublishedAtUtc),
            publishedBy = i.PublishedByUserId is { } u ? names.GetValueOrDefault(u) : null,
            updatedAt = TruthJson.Utc(i.UpdatedAtUtc),
            unpublishedChanges = i.Status == TrIssueStatus.Published && i.PublishedAtUtc < i.UpdatedAtUtc,
            items = counts.GetValueOrDefault(i.Id),
        }).ToList();
    }

    public async Task<TrResult<TrIssue>> SaveIssueAsync(int? id, IssueInput input, User user, CancellationToken ct)
    {
        var slug = input.Slug?.Trim().ToLowerInvariant() ?? "";
        if (!Slug().IsMatch(slug)) return TrResult<TrIssue>.Invalid("The slug is lower-case letters, digits and dashes, like 2026-w43.");
        if (!IsDate(input.WeekStart) || !IsDate(input.WeekEnd) || string.CompareOrdinal(input.WeekStart, input.WeekEnd) > 0)
            return TrResult<TrIssue>.Invalid("The week is two dates, yyyy-MM-dd, the start not after the end.");
        if ((input.TitleBn?.Length ?? 0) > 200 || (input.TitleEn?.Length ?? 0) > 200) return TrResult<TrIssue>.Invalid("A title is up to 200 characters.");
        if (await db.TrIssues.AnyAsync(i => i.Slug == slug && i.Id != (id ?? 0), ct)) return TrResult<TrIssue>.Conflict($"An issue with the slug {slug} exists.");

        TrIssue issue;
        var now = DateTime.UtcNow;
        if (id is null)
        {
            db.TrIssues.Add(issue = new TrIssue { CreatedByUserId = user.Id, CreatedAtUtc = now });
        }
        else
        {
            issue = (await db.TrIssues.FindAsync(new object[] { id.Value }, ct))!;
            if (issue is null) return TrResult<TrIssue>.NotFound("No such issue.");
            // The slug is in shared links once an issue has been out.
            if (issue.FirstPublishedAtUtc is not null && issue.Slug != slug)
                return TrResult<TrIssue>.Invalid("This issue has been published, so its slug is in shared links and cannot change.");
        }
        issue.Slug = slug;
        issue.TitleBn = input.TitleBn?.Trim() ?? "";
        issue.TitleEn = Trim(input.TitleEn);
        issue.SubtitleBn = Trim(input.SubtitleBn);
        issue.SubtitleEn = Trim(input.SubtitleEn);
        issue.WeekStart = input.WeekStart;
        issue.WeekEnd = input.WeekEnd;
        issue.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        return TrResult<TrIssue>.Ok(issue);
    }

    public async Task<TrResult<bool>> DeleteIssueAsync(int id, CancellationToken ct)
    {
        var issue = await db.TrIssues.FindAsync(new object[] { id }, ct);
        if (issue is null) return TrResult<bool>.NotFound("No such issue.");
        if (await db.TrSnapshots.AnyAsync(s => s.IssueId == id, ct))
            return TrResult<bool>.Invalid("This issue has been published. Withdraw it instead; what was published is kept.");
        var itemIds = await db.TrItems.Where(i => i.IssueId == id).Select(i => i.Id).ToListAsync(ct);
        await DeleteChildrenAsync(itemIds, includeHistory: true, ct);
        db.TrItems.RemoveRange(db.TrItems.Where(i => i.IssueId == id));
        db.TrIssues.Remove(issue);
        await db.SaveChangesAsync(ct);
        return TrResult<bool>.Ok(true);
    }

    /// <summary>The issue with every item and everything under it — the editor's view.</summary>
    public async Task<TrResult<object>> GetIssueAsync(int id, CancellationToken ct)
    {
        var c = await LoadAsync(id, ct);
        if (c is null) return TrResult<object>.NotFound("No such issue.");
        var userIds = c.StatusEvents.SelectMany(g => g).Select(e => e.ByUserId)
            .Concat(c.Corrections.SelectMany(g => g).Select(x => x.ByUserId))
            .Concat(c.Items.SelectMany(i => TruthJson.Hand(i.HandVerifiedJson)).Select(h => h.ByUserId))
            .Append(c.Issue.PublishedByUserId ?? 0);
        var names = await UserNamesAsync(userIds, ct);
        var snapshots = await db.TrSnapshots.AsNoTracking().Where(s => s.IssueId == id).OrderByDescending(s => s.CreatedAtUtc)
            .Select(s => new { s.Id, s.CreatedAtUtc, s.PublishedByUserId, s.SiteContentVersion }).ToListAsync(ct);
        var snapNames = await UserNamesAsync(snapshots.Select(s => s.PublishedByUserId), ct);

        return TrResult<object>.Ok(new
        {
            issue = new
            {
                c.Issue.Id, c.Issue.Slug, c.Issue.TitleBn, c.Issue.TitleEn, c.Issue.SubtitleBn, c.Issue.SubtitleEn,
                c.Issue.WeekStart, c.Issue.WeekEnd, c.Issue.Status,
                firstPublishedAt = TruthJson.Utc(c.Issue.FirstPublishedAtUtc),
                publishedAt = TruthJson.Utc(c.Issue.PublishedAtUtc),
                publishedBy = c.Issue.PublishedByUserId is { } p ? names.GetValueOrDefault(p) : null,
                updatedAt = TruthJson.Utc(c.Issue.UpdatedAtUtc),
                unpublishedChanges = c.Issue.Status == TrIssueStatus.Published && c.Issue.PublishedAtUtc < c.Issue.UpdatedAtUtc,
            },
            items = c.Items.Select(i => new
            {
                i.Id, i.TechId, i.Severity, i.Status, i.HeadlineBn, i.HeadlineEn, i.SummaryBn, i.SummaryEn,
                i.ImpactBn, i.ImpactEn, i.WhatToDoBn, i.WhatToDoEn,
                patchScheduledAt = TruthJson.Utc(i.PatchScheduledAtUtc),
                tags = TruthJson.Strings(i.TagsJson), i.AuthorName, i.Order,
                updatedAt = TruthJson.Utc(i.UpdatedAtUtc),
                handVerified = TruthJson.Hand(i.HandVerifiedJson).Select(h => new { h.Token, h.Note, by = names.GetValueOrDefault(h.ByUserId), at = TruthJson.Utc(h.AtUtc) }),
                advisories = c.Advisories[i.Id].OrderBy(a => a.Order).Select(a => new
                {
                    a.CveId, a.Cvss, a.TitleBn, a.TitleEn, a.AffectedRange, fixedVersions = TruthJson.Strings(a.FixedVersionsJson), a.ExploitedInWild, a.KevListed,
                }),
                timeline = c.Timeline[i.Id].OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.Id).Select(e => new { occurredAt = TruthJson.Utc(e.OccurredAtUtc), e.Level, e.TextBn, e.TextEn }),
                sources = c.Sources[i.Id].OrderBy(s => s.Order).ThenBy(s => s.Id).Select(s => new
                {
                    s.Url, s.Publisher, s.Author, s.Title, s.PublishedOn, s.Kind, s.FetchedText, fetchedAt = TruthJson.Utc(s.FetchedAtUtc),
                }),
                statusEvents = c.StatusEvents[i.Id].OrderBy(e => e.AtUtc).ThenBy(e => e.Id).Select(e => new
                {
                    at = TruthJson.Utc(e.AtUtc), from = e.FromStatus, to = e.ToStatus, e.TextBn, e.TextEn, by = names.GetValueOrDefault(e.ByUserId),
                }),
                corrections = c.Corrections[i.Id].OrderBy(x => x.AtUtc).Select(x => new { at = TruthJson.Utc(x.AtUtc), x.TextBn, x.TextEn, by = names.GetValueOrDefault(x.ByUserId) }),
            }),
            snapshots = snapshots.Select(s => new { s.Id, at = TruthJson.Utc(s.CreatedAtUtc), by = snapNames.GetValueOrDefault(s.PublishedByUserId), s.SiteContentVersion }),
        });
    }

    // ─────────────────────────── items ───────────────────────────

    public async Task<TrResult<int>> CreateItemAsync(int issueId, ItemInput input, User user, CancellationToken ct)
    {
        var issue = await db.TrIssues.FindAsync(new object[] { issueId }, ct);
        if (issue is null) return TrResult<int>.NotFound("No such issue.");
        var status = input.Status ?? TrStatus.Ongoing;
        if (!TrStatus.All.Contains(status)) return TrResult<int>.Invalid($"The status is one of: {string.Join(", ", TrStatus.All)}.");
        var now = DateTime.UtcNow;
        var item = new TrItem { IssueId = issueId, Status = status, CreatedAtUtc = now };
        if (await ApplyItemAsync(item, input, ct) is { } error) return TrResult<int>.Invalid(error);
        db.TrItems.Add(item);
        await db.SaveChangesAsync(ct);
        await ReplaceChildrenAsync(item.Id, input, ct);
        // The first status is logged too, so the public history starts where the item does.
        db.TrStatusEvents.Add(new TrStatusEvent { ItemId = item.Id, FromStatus = null, ToStatus = status, AtUtc = now, ByUserId = user.Id });
        issue.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        return TrResult<int>.Ok(item.Id);
    }

    public async Task<TrResult<int>> UpdateItemAsync(int itemId, ItemInput input, CancellationToken ct)
    {
        var item = await db.TrItems.FindAsync(new object[] { itemId }, ct);
        if (item is null) return TrResult<int>.NotFound("No such item.");
        if (await ApplyItemAsync(item, input, ct) is { } error) return TrResult<int>.Invalid(error);
        await ReplaceChildrenAsync(item.Id, input, ct);
        await TouchIssueAsync(item.IssueId, ct);
        await db.SaveChangesAsync(ct);
        return TrResult<int>.Ok(item.Id);
    }

    public async Task<TrResult<bool>> DeleteItemAsync(int itemId, CancellationToken ct)
    {
        var item = await db.TrItems.FindAsync(new object[] { itemId }, ct);
        if (item is null) return TrResult<bool>.NotFound("No such item.");
        var snaps = await db.TrSnapshots.AsNoTracking().Where(s => s.IssueId == item.IssueId).Select(s => s.Json).ToListAsync(ct);
        if (snaps.Any(j => TruthSnapshot.Wording(JsonNode.Parse(j)!.AsObject()).ContainsKey(itemId)))
            return TrResult<bool>.Invalid("This item has been published. Change its status, or add a correction; it cannot disappear silently (rule 5).");
        await DeleteChildrenAsync(new List<int> { itemId }, includeHistory: true, ct);
        db.TrItems.Remove(item);
        await TouchIssueAsync(item.IssueId, ct);
        await db.SaveChangesAsync(ct);
        return TrResult<bool>.Ok(true);
    }

    /// <summary>A status change, logged with its time (rule 5). patch_scheduled needs a time.</summary>
    public async Task<TrResult<int>> ChangeStatusAsync(int itemId, StatusInput input, User user, CancellationToken ct)
    {
        var item = await db.TrItems.FindAsync(new object[] { itemId }, ct);
        if (item is null) return TrResult<int>.NotFound("No such item.");
        if (!TrStatus.All.Contains(input.Status)) return TrResult<int>.Invalid($"The status is one of: {string.Join(", ", TrStatus.All)}.");
        if (input.Status == item.Status) return TrResult<int>.Invalid($"The item is already {item.Status}.");
        var patchAt = input.PatchScheduledAt is { } p ? AsUtc(p) : item.PatchScheduledAtUtc;
        if (input.Status == TrStatus.PatchScheduled && patchAt is null)
            return TrResult<int>.Invalid("A scheduled patch needs its time.");
        var now = DateTime.UtcNow;
        db.TrStatusEvents.Add(new TrStatusEvent
        {
            ItemId = item.Id, FromStatus = item.Status, ToStatus = input.Status, AtUtc = now,
            TextBn = Trim(input.TextBn), TextEn = Trim(input.TextEn), ByUserId = user.Id,
        });
        item.Status = input.Status;
        item.PatchScheduledAtUtc = patchAt;
        item.UpdatedAtUtc = now;
        await TouchIssueAsync(item.IssueId, ct);
        await db.SaveChangesAsync(ct);
        return TrResult<int>.Ok(item.Id);
    }

    /// <summary>A visible correction (rule 5). Append-only.</summary>
    public async Task<TrResult<int>> AddCorrectionAsync(int itemId, CorrectionInput input, User user, CancellationToken ct)
    {
        var item = await db.TrItems.FindAsync(new object[] { itemId }, ct);
        if (item is null) return TrResult<int>.NotFound("No such item.");
        if (string.IsNullOrWhiteSpace(input.TextBn)) return TrResult<int>.Invalid("A correction needs its Bangla text.");
        if (input.TextBn.Length > 1000 || (input.TextEn?.Length ?? 0) > 1000) return TrResult<int>.Invalid("A correction is up to 1000 characters.");
        db.TrCorrections.Add(new TrCorrection { ItemId = itemId, AtUtc = DateTime.UtcNow, TextBn = input.TextBn.Trim(), TextEn = Trim(input.TextEn), ByUserId = user.Id });
        await TouchIssueAsync(item.IssueId, ct);
        await db.SaveChangesAsync(ct);
        return TrResult<int>.Ok(itemId);
    }

    /// <summary>Marks a number "verified by hand" (rule 4). Only a number the check reports, with a note.</summary>
    public async Task<TrResult<int>> VerifyNumberAsync(int itemId, VerifyInput input, User user, CancellationToken ct)
    {
        var item = await db.TrItems.FindAsync(new object[] { itemId }, ct);
        if (item is null) return TrResult<int>.NotFound("No such item.");
        if (string.IsNullOrWhiteSpace(input.Note) || input.Note.Trim().Length < 5)
            return TrResult<int>.Invalid("Say how it was verified (at least 5 characters).");
        var findings = await NumbersAsync(item, ct);
        if (!findings.Any(f => f.Token == input.Token))
            return TrResult<int>.Invalid("That number is not in this item.");
        var list = TruthJson.Hand(item.HandVerifiedJson).Where(h => h.Token != input.Token).ToList();
        list.Add(new TruthJson.HandVerified(input.Token, input.Note.Trim(), user.Id, DateTime.UtcNow));
        item.HandVerifiedJson = TruthJson.FromHand(list);
        await db.SaveChangesAsync(ct);
        return TrResult<int>.Ok(itemId);
    }

    public async Task<TrResult<int>> UnverifyNumberAsync(int itemId, string token, CancellationToken ct)
    {
        var item = await db.TrItems.FindAsync(new object[] { itemId }, ct);
        if (item is null) return TrResult<int>.NotFound("No such item.");
        item.HandVerifiedJson = TruthJson.FromHand(TruthJson.Hand(item.HandVerifiedJson).Where(h => h.Token != token));
        await db.SaveChangesAsync(ct);
        return TrResult<int>.Ok(itemId);
    }

    private async Task<string?> ApplyItemAsync(TrItem item, ItemInput input, CancellationToken ct)
    {
        if (!await db.TrTechs.AnyAsync(t => t.Id == input.TechId, ct)) return "Choose a technology.";
        if (!TrSeverity.All.Contains(input.Severity)) return $"The severity is one of: {string.Join(", ", TrSeverity.All)}.";
        if (Over(input.HeadlineBn, 200) || Over(input.HeadlineEn, 200)) return "A headline is up to 200 characters.";
        if (Over(input.SummaryBn, 4000) || Over(input.SummaryEn, 4000)) return "A summary is up to 4000 characters.";
        if (Over(input.ImpactBn, 2000) || Over(input.ImpactEn, 2000) || Over(input.WhatToDoBn, 2000) || Over(input.WhatToDoEn, 2000))
            return "Impact and what-to-do are up to 2000 characters each.";
        if (Over(input.AuthorName, 80)) return "The byline is up to 80 characters.";
        if ((input.Tags?.Count ?? 0) > 8 || input.Tags?.Any(t => t.Length > 30) == true) return "Up to 8 tags, 30 characters each.";
        if ((input.Advisories?.Count ?? 0) > 20 || (input.Timeline?.Count ?? 0) > 40 || (input.Sources?.Count ?? 0) > 20)
            return "Up to 20 advisories, 40 timeline entries and 20 sources.";
        foreach (var a in input.Advisories ?? new())
        {
            if (!string.IsNullOrWhiteSpace(a.CveId) && !CveId().IsMatch(a.CveId.Trim().ToUpperInvariant())) return $"{a.CveId} is not a CVE ID (CVE-yyyy-nnnn).";
            if (a.Cvss is < 0 or > 10) return "A CVSS score is 0–10.";
        }
        foreach (var e in input.Timeline ?? new())
            if (!TrTimelineEvent.Levels.Contains(e.Level)) return $"A timeline level is one of: {string.Join(", ", TrTimelineEvent.Levels)}.";
        foreach (var s in input.Sources ?? new())
        {
            if (!IsHttps(s.Url)) return $"A source link must be https: {s.Url}";
            if (!TrSourceKind.All.Contains(s.Kind)) return $"A source kind is one of: {string.Join(", ", TrSourceKind.All)}.";
            if (!string.IsNullOrWhiteSpace(s.PublishedOn) && !IsDate(s.PublishedOn)) return "A source's date is yyyy-MM-dd.";
            if ((s.FetchedText?.Length ?? 0) > 200_000) return "A source's text is up to 200,000 characters.";
        }

        item.TechId = input.TechId;
        item.Severity = input.Severity;
        item.HeadlineBn = input.HeadlineBn?.Trim() ?? "";
        item.HeadlineEn = Trim(input.HeadlineEn);
        item.SummaryBn = input.SummaryBn?.Trim() ?? "";
        item.SummaryEn = Trim(input.SummaryEn);
        item.ImpactBn = Trim(input.ImpactBn);
        item.ImpactEn = Trim(input.ImpactEn);
        item.WhatToDoBn = Trim(input.WhatToDoBn);
        item.WhatToDoEn = Trim(input.WhatToDoEn);
        item.PatchScheduledAtUtc = input.PatchScheduledAt is { } p ? AsUtc(p) : null;
        item.TagsJson = TruthJson.FromStrings(input.Tags);
        item.AuthorName = input.AuthorName?.Trim() ?? "";
        item.Order = input.Order;
        item.UpdatedAtUtc = DateTime.UtcNow;
        return null;
    }

    private async Task ReplaceChildrenAsync(int itemId, ItemInput input, CancellationToken ct)
    {
        // A source keeps its fetch time while its text is the same.
        var fetched = await db.TrSources.AsNoTracking().Where(s => s.ItemId == itemId)
            .Select(s => new { s.Url, s.FetchedText, s.FetchedAtUtc }).ToListAsync(ct);
        await DeleteChildrenAsync(new List<int> { itemId }, includeHistory: false, ct);
        var now = DateTime.UtcNow;
        var i = 0;
        foreach (var a in input.Advisories ?? new())
            db.TrAdvisories.Add(new TrAdvisory
            {
                ItemId = itemId, Order = i++, CveId = Trim(a.CveId)?.ToUpperInvariant(), Cvss = a.Cvss, TitleBn = Trim(a.TitleBn), TitleEn = Trim(a.TitleEn),
                AffectedRange = Trim(a.AffectedRange), FixedVersionsJson = TruthJson.FromStrings(a.FixedVersions),
                ExploitedInWild = a.ExploitedInWild, KevListed = a.KevListed,
            });
        foreach (var e in input.Timeline ?? new())
            db.TrTimelineEvents.Add(new TrTimelineEvent { ItemId = itemId, OccurredAtUtc = AsUtc(e.OccurredAt), Level = e.Level, TextBn = e.TextBn?.Trim() ?? "", TextEn = Trim(e.TextEn) });
        i = 0;
        foreach (var s in input.Sources ?? new())
        {
            var text = s.FetchedText ?? "";
            var same = fetched.FirstOrDefault(f => f.Url == s.Url.Trim() && f.FetchedText == text);
            db.TrSources.Add(new TrSource
            {
                ItemId = itemId, Order = i++, Url = s.Url.Trim(), Publisher = s.Publisher?.Trim() ?? "", Author = Trim(s.Author),
                Title = s.Title?.Trim() ?? "", PublishedOn = Trim(s.PublishedOn), Kind = s.Kind, FetchedText = text,
                FetchedAtUtc = text.Length == 0 ? null : same?.FetchedAtUtc ?? now,
            });
        }
    }

    private async Task DeleteChildrenAsync(List<int> itemIds, bool includeHistory, CancellationToken ct)
    {
        db.TrAdvisories.RemoveRange(await db.TrAdvisories.Where(a => itemIds.Contains(a.ItemId)).ToListAsync(ct));
        db.TrTimelineEvents.RemoveRange(await db.TrTimelineEvents.Where(e => itemIds.Contains(e.ItemId)).ToListAsync(ct));
        db.TrSources.RemoveRange(await db.TrSources.Where(s => s.ItemId != null && itemIds.Contains(s.ItemId.Value)).ToListAsync(ct));
        if (!includeHistory) return;
        db.TrStatusEvents.RemoveRange(await db.TrStatusEvents.Where(e => itemIds.Contains(e.ItemId)).ToListAsync(ct));
        db.TrCorrections.RemoveRange(await db.TrCorrections.Where(c => itemIds.Contains(c.ItemId)).ToListAsync(ct));
    }

    private async Task TouchIssueAsync(int issueId, CancellationToken ct)
    {
        if (await db.TrIssues.FindAsync(new object[] { issueId }, ct) is { } issue) issue.UpdatedAtUtc = DateTime.UtcNow;
    }

    // ─────────────────────────── the checklist ───────────────────────────

    private async Task<List<NumberFinding>> NumbersAsync(TrItem item, CancellationToken ct)
    {
        var advisories = await db.TrAdvisories.AsNoTracking().Where(a => a.ItemId == item.Id).OrderBy(a => a.Order).ToListAsync(ct);
        var timeline = await db.TrTimelineEvents.AsNoTracking().Where(e => e.ItemId == item.Id).OrderBy(e => e.OccurredAtUtc).ToListAsync(ct);
        var sources = await db.TrSources.AsNoTracking().Where(s => s.ItemId == item.Id).ToListAsync(ct);
        return NumberCheck.Run(item, advisories, timeline, sources, HandMap(item));
    }

    private static Dictionary<string, string> HandMap(TrItem item) =>
        TruthJson.Hand(item.HandVerifiedJson).GroupBy(h => h.Token).ToDictionary(g => g.Key, g => g.Last().Note);

    /// <summary>The pre-publish checklist (§8) and every item's number check. Publishing needs no problems.</summary>
    public async Task<TrResult<object>> CheckAsync(int issueId, CancellationToken ct)
    {
        var c = await LoadAsync(issueId, ct);
        if (c is null) return TrResult<object>.NotFound("No such issue.");
        var (problems, numbers) = await ChecklistAsync(c, ct);
        return TrResult<object>.Ok(new
        {
            ok = problems.Count == 0,
            problems,
            items = c.Items.Select(i => new { itemId = i.Id, numbers = numbers[i.Id] }),
        });
    }

    private async Task<(List<TrProblem> Problems, Dictionary<int, List<NumberFinding>> Numbers)> ChecklistAsync(IssueContent c, CancellationToken ct)
    {
        var problems = new List<TrProblem>();
        var numbers = new Dictionary<int, List<NumberFinding>>();
        if (string.IsNullOrWhiteSpace(c.Issue.TitleBn)) problems.Add(new(null, "bangla", "The issue's Bangla title is empty.", "titleBn"));
        if (!string.IsNullOrWhiteSpace(c.Issue.SubtitleEn) && string.IsNullOrWhiteSpace(c.Issue.SubtitleBn))
            problems.Add(new(null, "bangla", "The subtitle has English but no Bangla.", "subtitleBn"));
        if (c.Items.Count == 0) problems.Add(new(null, "noItems", "The issue has no items."));
        if (PrivateIp().IsMatch($"{c.Issue.TitleBn} {c.Issue.TitleEn} {c.Issue.SubtitleBn} {c.Issue.SubtitleEn}"))
            problems.Add(new(null, "privateIp", "A private IP address is in the issue's text. Never publish our own infrastructure (rule 7)."));

        foreach (var item in c.Items)
        {
            var sources = c.Sources[item.Id].ToList();
            var advisories = c.Advisories[item.Id].OrderBy(a => a.Order).ToList();
            var timeline = c.Timeline[item.Id].OrderBy(e => e.OccurredAtUtc).ToList();
            void Add(string code, string message, string? field = null) => problems.Add(new(item.Id, code, message, field));

            // Rule 1: public sources, each with a link, a publisher and a title.
            if (sources.Count == 0) Add("noSource", "No source. Every item needs at least one public source (rule 1).");
            foreach (var (s, n) in sources.Select((s, n) => (s, n + 1)))
            {
                if (!IsHttps(s.Url) || string.IsNullOrWhiteSpace(s.Publisher) || string.IsNullOrWhiteSpace(s.Title))
                    Add("sourceIncomplete", $"Source [{n}] needs an https link, a publisher and a title.", $"sources[{n}]");
            }

            // Bangla is required; English is optional, but English without Bangla is not allowed.
            if (string.IsNullOrWhiteSpace(item.HeadlineBn)) Add("bangla", "The Bangla headline is empty.", "headlineBn");
            if (string.IsNullOrWhiteSpace(item.SummaryBn)) Add("bangla", "The Bangla summary is empty.", "summaryBn");
            foreach (var (bn, en, field) in new[] { (item.ImpactBn, item.ImpactEn, "impactBn"), (item.WhatToDoBn, item.WhatToDoEn, "whatToDoBn") })
                if (!string.IsNullOrWhiteSpace(en) && string.IsNullOrWhiteSpace(bn)) Add("bangla", $"{field} is empty but its English is filled in.", field);
            foreach (var (a, n) in advisories.Select((a, n) => (a, n + 1)))
                if (!string.IsNullOrWhiteSpace(a.TitleEn) && string.IsNullOrWhiteSpace(a.TitleBn)) Add("bangla", $"Advisory {n} has an English title but no Bangla.", $"advisories[{n}].titleBn");
            foreach (var (e, n) in timeline.Select((e, n) => (e, n + 1)))
                if (string.IsNullOrWhiteSpace(e.TextBn)) Add("bangla", $"Timeline entry {n} has no Bangla text.", $"timeline[{n}].textBn");
            foreach (var e in c.StatusEvents[item.Id])
                if (!string.IsNullOrWhiteSpace(e.TextEn) && string.IsNullOrWhiteSpace(e.TextBn)) Add("bangla", "A status change has English text but no Bangla.");
            foreach (var x in c.Corrections[item.Id])
                if (string.IsNullOrWhiteSpace(x.TextBn)) Add("bangla", "A correction has no Bangla text.");

            // Rule 4.
            var found = NumberCheck.Run(item, advisories, timeline, sources, HandMap(item));
            numbers[item.Id] = found;
            foreach (var f in found.Where(f => !f.Ok))
                Add("numbers", $"{f.Token} ({f.Kind}, in {string.Join(", ", f.Fields)}) is not in any source. Fix it, or verify it by hand with a note.", f.Fields.FirstOrDefault());

            if (item.Status == TrStatus.PatchScheduled && item.PatchScheduledAtUtc is null)
                Add("patchTime", "The patch is scheduled but has no time.", "patchScheduledAt");
            if (TrStatus.IsUrgent(item.Status) && string.IsNullOrWhiteSpace(item.WhatToDoBn))
                Add("urgentNote", "An urgent item needs a fix guide or a \"what to do\" note.", "whatToDoBn");

            // Rule 3: what only an attacker claims is unconfirmed.
            if (sources.Count > 0 && sources.All(s => s.Kind == TrSourceKind.Claim) && item.Status != TrStatus.Unconfirmed)
                Add("claimOnly", "Every source is a claim, so the status must be \"unconfirmed\" (rule 3).");

            // References: every [n] in the text points to a source.
            foreach (var text in new[] { item.SummaryBn, item.SummaryEn, item.ImpactBn, item.ImpactEn, item.WhatToDoBn, item.WhatToDoEn })
            foreach (Match m in Citation().Matches(text ?? ""))
                if (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) is var n && (n < 1 || n > sources.Count))
                    Add("citation", $"[{n}] cites a source the item does not have ({sources.Count} listed).");

            // Rule 7.
            if (NumberCheck.PublicFields(item, advisories, timeline).Any(f => PrivateIp().IsMatch(f.Text)))
                Add("privateIp", "A private IP address is in the item's text. Never publish our own infrastructure (rule 7).");
        }

        // Rule 5: what was published and has since changed its words needs a visible correction.
        var previous = await db.TrSnapshots.AsNoTracking().Where(s => s.IssueId == c.Issue.Id)
            .OrderByDescending(s => s.CreatedAtUtc).ThenByDescending(s => s.Id).FirstOrDefaultAsync(ct);
        if (previous is not null)
        {
            var before = TruthSnapshot.Wording(JsonNode.Parse(previous.Json)!.AsObject());
            var now = TruthSnapshot.Wording(TruthSnapshot.Build(c, 0, DateTime.UtcNow));
            foreach (var (id, words) in now)
            {
                if (!before.TryGetValue(id, out var old) || old == words) continue;
                if (!c.Corrections[id].Any(x => x.AtUtc > previous.CreatedAtUtc))
                    problems.Add(new(id, "correctionNeeded", "This item's published text has changed. Add a correction saying what changed (rule 5)."));
            }
        }
        return (problems, numbers);
    }

    // ─────────────────────────── review and publish ───────────────────────────

    /// <summary>A person publishes (§11): the checklist must be clean, then one immutable snapshot is written.</summary>
    public async Task<TrResult<object>> PublishAsync(int issueId, User user, CancellationToken ct)
    {
        var c = await LoadAsync(issueId, ct);
        if (c is null) return TrResult<object>.NotFound("No such issue.");
        var (problems, _) = await ChecklistAsync(c, ct);
        if (problems.Count > 0) return TrResult<object>.Invalid("The pre-publish checklist is not clean.", problems);

        var now = DateTime.UtcNow;
        var siteVersion = await db.TrSiteContents.AsNoTracking().Where(s => s.Kind == TruthContent.Site && s.Status == TrSiteContent.StatusPublished)
            .MaxAsync(s => (int?)s.Version, ct) ?? 0;
        var json = TruthSnapshot.Build(c, siteVersion, now).ToJsonString();
        var urgent = c.Items.Any(i => TrStatus.IsUrgent(i.Status));
        db.TrSnapshots.Add(new TrSnapshot
        {
            IssueId = c.Issue.Id, IssueSlug = c.Issue.Slug, Json = json, ETag = TruthSnapshot.ETag(json),
            SiteContentVersion = siteVersion, Urgent = urgent, CreatedAtUtc = now, PublishedByUserId = user.Id,
        });
        var issue = (await db.TrIssues.FindAsync(new object[] { issueId }, ct))!;
        issue.Status = TrIssueStatus.Published;
        issue.FirstPublishedAtUtc ??= now;
        issue.PublishedAtUtc = now;
        issue.PublishedByUserId = user.Id;
        issue.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        return TrResult<object>.Ok(new { publishedAt = TruthJson.Utc(now), urgent });
    }

    /// <summary>Takes an issue off the public site. Its snapshots stay, unserved; publishing again brings it back.</summary>
    public async Task<TrResult<bool>> WithdrawAsync(int issueId, CancellationToken ct)
    {
        var issue = await db.TrIssues.FindAsync(new object[] { issueId }, ct);
        if (issue is null) return TrResult<bool>.NotFound("No such issue.");
        if (issue.Status != TrIssueStatus.Published) return TrResult<bool>.Invalid("The issue is not published.");
        issue.Status = TrIssueStatus.Draft;
        issue.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return TrResult<bool>.Ok(true);
    }

    /// <summary>The snapshot publishing would write now — the editor's preview. Never stored, never public.</summary>
    public async Task<TrResult<JsonObject>> PreviewAsync(int issueId, CancellationToken ct)
    {
        var c = await LoadAsync(issueId, ct);
        if (c is null) return TrResult<JsonObject>.NotFound("No such issue.");
        var siteVersion = await db.TrSiteContents.AsNoTracking().Where(s => s.Kind == TruthContent.Site && s.Status == TrSiteContent.StatusPublished).MaxAsync(s => (int?)s.Version, ct) ?? 0;
        return TrResult<JsonObject>.Ok(TruthSnapshot.Build(c, siteVersion, DateTime.UtcNow));
    }

    private async Task<IssueContent?> LoadAsync(int issueId, CancellationToken ct)
    {
        var issue = await db.TrIssues.AsNoTracking().FirstOrDefaultAsync(i => i.Id == issueId, ct);
        if (issue is null) return null;
        var items = await db.TrItems.AsNoTracking().Where(i => i.IssueId == issueId).OrderBy(i => i.Order).ThenBy(i => i.Id).ToListAsync(ct);
        var ids = items.Select(i => i.Id).ToList();
        var techIds = items.Select(i => i.TechId).Distinct().ToList();
        return new IssueContent(
            issue, items,
            await db.TrTechs.AsNoTracking().Where(t => techIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct),
            (await db.TrAdvisories.AsNoTracking().Where(a => ids.Contains(a.ItemId)).ToListAsync(ct)).ToLookup(a => a.ItemId),
            (await db.TrTimelineEvents.AsNoTracking().Where(e => ids.Contains(e.ItemId)).ToListAsync(ct)).ToLookup(e => e.ItemId),
            (await db.TrSources.AsNoTracking().Where(s => s.ItemId != null && ids.Contains(s.ItemId.Value)).ToListAsync(ct)).ToLookup(s => s.ItemId!.Value),
            (await db.TrStatusEvents.AsNoTracking().Where(e => ids.Contains(e.ItemId)).ToListAsync(ct)).ToLookup(e => e.ItemId),
            (await db.TrCorrections.AsNoTracking().Where(x => ids.Contains(x.ItemId)).ToListAsync(ct)).ToLookup(x => x.ItemId));
    }

    // ─────────────────────────── site text, memes, glossary ───────────────────────────
    // One mechanism for the three editable documents (Truth/TruthContent.cs): a draft,
    // published versions, rollback. Each kind counts its own versions.

    /// <summary>The editor's state for one kind: the default, the schema, what is published, the draft and the history.</summary>
    public async Task<TrResult<object>> ContentStateAsync(string kind, CancellationToken ct)
    {
        if (!TruthContent.IsKind(kind)) return TrResult<object>.NotFound($"No content kind {kind}.");
        var rows = await db.TrSiteContents.AsNoTracking().Where(r => r.Kind == kind).OrderByDescending(r => r.Version).ThenByDescending(r => r.Id).ToListAsync(ct);
        var names = await UserNamesAsync(rows.Select(r => r.EditedByUserId), ct);
        var published = rows.FirstOrDefault(r => r.Status == TrSiteContent.StatusPublished);
        var draft = rows.FirstOrDefault(r => r.Status == TrSiteContent.StatusDraft);
        object? Row(TrSiteContent? r) => r is null ? null : new
        {
            r.Version, text = JsonNode.Parse(r.Json), r.Note, editedBy = names.GetValueOrDefault(r.EditedByUserId), editedAt = TruthJson.Utc(r.EditedAtUtc),
        };
        return TrResult<object>.Ok(new
        {
            kind,
            @default = TruthContent.Default(kind),
            schema = TruthContent.Schema(kind),
            published = Row(published),
            draft = Row(draft),
            history = rows.Where(r => r.Status == TrSiteContent.StatusPublished).Select(r => new
            {
                r.Version, r.Note, editedBy = names.GetValueOrDefault(r.EditedByUserId), editedAt = TruthJson.Utc(r.EditedAtUtc),
            }),
        });
    }

    /// <summary>A draft is kept whatever its problems, so work is never lost; they come back with it.</summary>
    public async Task<TrResult<object>> SaveContentDraftAsync(string kind, JsonObject? text, User user, CancellationToken ct)
    {
        if (!TruthContent.IsKind(kind)) return TrResult<object>.NotFound($"No content kind {kind}.");
        if (text is null) return TrResult<object>.Invalid("Send the document.");
        var errors = TruthContent.Validate(kind, text);
        var draft = await db.TrSiteContents.FirstOrDefaultAsync(r => r.Kind == kind && r.Status == TrSiteContent.StatusDraft, ct);
        if (draft is null) db.TrSiteContents.Add(draft = new TrSiteContent { Kind = kind, Status = TrSiteContent.StatusDraft });
        draft.Json = text.ToJsonString();
        draft.SchemaVersion = TruthContent.SchemaVersion(kind);
        draft.EditedByUserId = user.Id;
        draft.EditedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return TrResult<object>.Ok(new { saved = true, errors });
    }

    /// <summary>Validated against the kind's schema, then published as its next version (§5). The draft goes.</summary>
    public async Task<TrResult<object>> PublishContentAsync(string kind, JsonObject? text, string? note, User user, CancellationToken ct)
    {
        if (!TruthContent.IsKind(kind)) return TrResult<object>.NotFound($"No content kind {kind}.");
        if (text is null) return TrResult<object>.Invalid("Send the document.");
        var errors = TruthContent.Validate(kind, text);
        if (errors.Count > 0)
            return TrResult<object>.Invalid($"The {kind} document has {errors.Count} problem(s).", errors.Select(e => new TrProblem(null, kind, e.Message, e.Path)).ToList());
        var version = await PublishContentRowAsync(kind, text.ToJsonString(), Trim(note), user, ct);
        return TrResult<object>.Ok(new { version });
    }

    /// <summary>One-click rollback: version N published again as the next version, so the history keeps both.</summary>
    public async Task<TrResult<object>> RollbackContentAsync(string kind, int version, User user, CancellationToken ct)
    {
        if (!TruthContent.IsKind(kind)) return TrResult<object>.NotFound($"No content kind {kind}.");
        var row = await db.TrSiteContents.AsNoTracking().FirstOrDefaultAsync(r => r.Kind == kind && r.Status == TrSiteContent.StatusPublished && r.Version == version, ct);
        if (row is null) return TrResult<object>.NotFound($"No published version {version}.");
        var errors = TruthContent.Validate(kind, JsonNode.Parse(row.Json));
        if (errors.Count > 0)
            return TrResult<object>.Invalid($"Version {version} no longer fits the schema ({errors.Count} problem(s)); edit it instead.",
                errors.Select(e => new TrProblem(null, kind, e.Message, e.Path)).ToList());
        var next = await PublishContentRowAsync(kind, row.Json, $"rollback to v{version}", user, ct);
        return TrResult<object>.Ok(new { version = next });
    }

    private async Task<int> PublishContentRowAsync(string kind, string json, string? note, User user, CancellationToken ct)
    {
        var next = (await db.TrSiteContents.Where(r => r.Kind == kind && r.Status == TrSiteContent.StatusPublished).MaxAsync(r => (int?)r.Version, ct) ?? 0) + 1;
        db.TrSiteContents.Add(new TrSiteContent
        {
            Kind = kind, Version = next, Json = json, SchemaVersion = TruthContent.SchemaVersion(kind), Status = TrSiteContent.StatusPublished,
            Note = note, EditedByUserId = user.Id, EditedAtUtc = DateTime.UtcNow,
        });
        db.TrSiteContents.RemoveRange(db.TrSiteContents.Where(r => r.Kind == kind && r.Status == TrSiteContent.StatusDraft));
        await db.SaveChangesAsync(ct);
        return next;
    }

    // ─────────────────────────── what the public reads ───────────────────────────

    /// <summary>The latest snapshot of the current issue: the published one with the latest week.</summary>
    public async Task<PublicSnapshot?> CurrentAsync(CancellationToken ct)
    {
        var issueId = await db.TrIssues.AsNoTracking().Where(i => i.Status == TrIssueStatus.Published)
            .OrderByDescending(i => i.WeekStart).ThenByDescending(i => i.PublishedAtUtc).Select(i => (int?)i.Id).FirstOrDefaultAsync(ct);
        return issueId is null ? null : await LatestSnapshotAsync(issueId.Value, ct);
    }

    public async Task<PublicSnapshot?> IssueAsync(string slug, CancellationToken ct)
    {
        var issueId = await db.TrIssues.AsNoTracking().Where(i => i.Status == TrIssueStatus.Published && i.Slug == slug)
            .Select(i => (int?)i.Id).FirstOrDefaultAsync(ct);
        return issueId is null ? null : await LatestSnapshotAsync(issueId.Value, ct);
    }

    private async Task<PublicSnapshot?> LatestSnapshotAsync(int issueId, CancellationToken ct) =>
        await db.TrSnapshots.AsNoTracking().Where(s => s.IssueId == issueId)
            .OrderByDescending(s => s.CreatedAtUtc).ThenByDescending(s => s.Id)
            .Select(s => new PublicSnapshot(s.Json, s.ETag, s.Urgent)).FirstOrDefaultAsync(ct);

    /// <summary>Every published issue, newest week first, read from its latest snapshot — never from the staff tables.</summary>
    public async Task<string> ArchiveJsonAsync(CancellationToken ct)
    {
        var ids = await db.TrIssues.AsNoTracking().Where(i => i.Status == TrIssueStatus.Published)
            .OrderByDescending(i => i.WeekStart).ThenByDescending(i => i.PublishedAtUtc).Select(i => i.Id).ToListAsync(ct);
        var list = new JsonArray();
        foreach (var id in ids)
        {
            if (await LatestSnapshotAsync(id, ct) is not { } snap) continue;
            var doc = JsonNode.Parse(snap.Json)!;
            var issue = doc["issue"]!;
            list.Add(new JsonObject
            {
                ["slug"] = issue["slug"]!.GetValue<string>(),
                ["titleBn"] = issue["titleBn"]?.GetValue<string>(),
                ["titleEn"] = issue["titleEn"]?.GetValue<string>(),
                ["weekStart"] = issue["weekStart"]!.GetValue<string>(),
                ["weekEnd"] = issue["weekEnd"]!.GetValue<string>(),
                ["publishedAt"] = issue["publishedAt"]!.GetValue<string>(),
                ["itemCount"] = doc["items"]!.AsArray().Count,
                ["urgent"] = snap.Urgent,
            });
        }
        return new JsonObject { ["issues"] = list }.ToJsonString();
    }

    /// <summary>The published version of one content kind, or null while only its built-in default exists.</summary>
    public async Task<(int Version, string Json)?> PublishedContentAsync(string kind, CancellationToken ct)
    {
        var row = await db.TrSiteContents.AsNoTracking().Where(r => r.Kind == kind && r.Status == TrSiteContent.StatusPublished)
            .OrderByDescending(r => r.Version).Select(r => new { r.Version, r.Json }).FirstOrDefaultAsync(ct);
        return row is null ? null : (row.Version, row.Json);
    }

    // ─────────────────────────── helpers ───────────────────────────

    private async Task<Dictionary<int, string>> UserNamesAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var set = ids.Where(i => i > 0).Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => set.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, ct);
    }

    private static bool IsHttps(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(u.Host);

    private static bool IsDate(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static DateTime AsUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
    };

    private static bool Over(string? s, int max) => (s?.Length ?? 0) > max;

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
