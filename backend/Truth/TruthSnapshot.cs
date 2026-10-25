// সত্য মন্ত্রণালয় — the public JSON (docs/TRUTH_PROMPT.md §4, gate G1.3).
//
// THE allow-list. A snapshot is built here field by field from the staff tables;
// nothing is serialised whole, so a column added to an entity later stays private
// until someone adds it below on purpose. Never here: a source's fetched text
// (third-party text, rule 2), hand-verification notes, status-change notes'
// authors, user ids or usernames, drafts. The only name is the item's byline,
// which staff write for the public.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Truth;

public sealed record IssueContent(
    TrIssue Issue,
    IReadOnlyList<TrItem> Items,
    IReadOnlyDictionary<int, TrTech> Techs,
    ILookup<int, TrAdvisory> Advisories,
    ILookup<int, TrTimelineEvent> Timeline,
    ILookup<int, TrSource> Sources,
    ILookup<int, TrStatusEvent> StatusEvents,
    ILookup<int, TrCorrection> Corrections);

public static class TruthSnapshot
{
    public const int SchemaVersion = 1;

    public static JsonObject Build(IssueContent c, int siteContentVersion, DateTime publishedAtUtc)
    {
        var items = new JsonArray();
        foreach (var item in c.Items)
        {
            var tech = c.Techs[item.TechId];
            var events = c.StatusEvents[item.Id].OrderBy(e => e.AtUtc).ThenBy(e => e.Id).ToList();
            items.Add(new JsonObject
            {
                ["id"] = item.Id,
                ["order"] = item.Order,
                ["tech"] = new JsonObject
                {
                    ["slug"] = tech.Slug,
                    ["name"] = tech.Name,
                    ["category"] = tech.Category,
                    ["monogram"] = tech.Monogram,
                    ["hue"] = tech.Hue,
                    ["homepage"] = tech.Homepage,
                    ["versionLines"] = new JsonArray(TruthJson.Strings(tech.VersionLinesJson).Select(v => (JsonNode?)v).ToArray()),
                    ["eolLines"] = new JsonArray(TruthJson.Strings(tech.EolLinesJson).Select(v => (JsonNode?)v).ToArray()),
                },
                ["severity"] = item.Severity,
                ["status"] = item.Status,
                ["statusSince"] = TruthJson.Utc(events.LastOrDefault()?.AtUtc),
                ["patchScheduledAt"] = TruthJson.Utc(item.PatchScheduledAtUtc),
                ["headlineBn"] = item.HeadlineBn,
                ["headlineEn"] = Blank(item.HeadlineEn),
                ["summaryBn"] = item.SummaryBn,
                ["summaryEn"] = Blank(item.SummaryEn),
                ["impactBn"] = Blank(item.ImpactBn),
                ["impactEn"] = Blank(item.ImpactEn),
                ["whatToDoBn"] = Blank(item.WhatToDoBn),
                ["whatToDoEn"] = Blank(item.WhatToDoEn),
                ["doNowCommand"] = Blank(item.DoNowCommand),
                ["doNowLanguage"] = Blank(item.DoNowLanguage),
                ["rotateKeys"] = JsonNode.Parse(string.IsNullOrWhiteSpace(item.RotateKeysJson) ? "[]" : item.RotateKeysJson),
                ["tags"] = new JsonArray(TruthJson.Strings(item.TagsJson).Select(t => (JsonNode?)t).ToArray()),
                ["author"] = item.AuthorName,
                ["advisories"] = new JsonArray(c.Advisories[item.Id].OrderBy(a => a.Order).Select(a => (JsonNode?)new JsonObject
                {
                    ["cveId"] = Blank(a.CveId),
                    ["cvss"] = a.Cvss,
                    ["titleBn"] = Blank(a.TitleBn),
                    ["titleEn"] = Blank(a.TitleEn),
                    ["affectedRange"] = Blank(a.AffectedRange),
                    ["fixedVersions"] = new JsonArray(TruthJson.Strings(a.FixedVersionsJson).Select(v => (JsonNode?)v).ToArray()),
                    ["exploitedInWild"] = a.ExploitedInWild,
                    ["kevListed"] = a.KevListed,
                }).ToArray()),
                ["timeline"] = new JsonArray(c.Timeline[item.Id].OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.Id).Select(e => (JsonNode?)new JsonObject
                {
                    ["at"] = TruthJson.Utc(e.OccurredAtUtc),
                    ["level"] = e.Level,
                    ["textBn"] = e.TextBn,
                    ["textEn"] = Blank(e.TextEn),
                }).ToArray()),
                ["statusEvents"] = new JsonArray(events.Select(e => (JsonNode?)new JsonObject
                {
                    ["at"] = TruthJson.Utc(e.AtUtc),
                    ["from"] = e.FromStatus,
                    ["to"] = e.ToStatus,
                    ["textBn"] = Blank(e.TextBn),
                    ["textEn"] = Blank(e.TextEn),
                }).ToArray()),
                ["sources"] = new JsonArray(c.Sources[item.Id].OrderBy(s => s.Order).ThenBy(s => s.Id).Select((s, i) => (JsonNode?)new JsonObject
                {
                    ["n"] = i + 1,
                    ["url"] = s.Url,
                    ["publisher"] = s.Publisher,
                    ["author"] = Blank(s.Author),
                    ["title"] = s.Title,
                    ["publishedOn"] = Blank(s.PublishedOn),
                    ["kind"] = s.Kind,
                }).ToArray()),
                ["corrections"] = new JsonArray(c.Corrections[item.Id].OrderBy(x => x.AtUtc).Select(x => (JsonNode?)new JsonObject
                {
                    ["at"] = TruthJson.Utc(x.AtUtc),
                    ["textBn"] = x.TextBn,
                    ["textEn"] = Blank(x.TextEn),
                }).ToArray()),
            });
        }

        return new JsonObject
        {
            ["schema"] = SchemaVersion,
            ["issue"] = new JsonObject
            {
                ["slug"] = c.Issue.Slug,
                ["titleBn"] = c.Issue.TitleBn,
                ["titleEn"] = Blank(c.Issue.TitleEn),
                ["subtitleBn"] = Blank(c.Issue.SubtitleBn),
                ["subtitleEn"] = Blank(c.Issue.SubtitleEn),
                ["weekStart"] = c.Issue.WeekStart,
                ["weekEnd"] = c.Issue.WeekEnd,
                ["firstPublishedAt"] = TruthJson.Utc(c.Issue.FirstPublishedAtUtc ?? publishedAtUtc),
                ["publishedAt"] = TruthJson.Utc(publishedAtUtc),
            },
            ["urgent"] = c.Items.Any(i => TrStatus.IsUrgent(i.Status)),
            ["siteContentVersion"] = siteContentVersion,
            ["items"] = items,
        };
    }

    /// <summary>A strong ETag over the exact bytes served.</summary>
    public static string ETag(string json) =>
        "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant() + "\"";

    /// <summary>
    /// What an item says in public, as one string per item — so a republish can
    /// tell which items changed their words since the last snapshot (rule 5: a
    /// change to what was published needs a visible correction). Status, timeline
    /// and sources are left out: a status change is logged on its own, and new
    /// timeline entries and sources are additions, not corrections.
    /// </summary>
    public static Dictionary<int, string> Wording(JsonObject snapshot)
    {
        var map = new Dictionary<int, string>();
        foreach (var node in snapshot["items"]?.AsArray() ?? new JsonArray())
        {
            if (node is not JsonObject item) continue;
            var parts = new[] { "headlineBn", "headlineEn", "summaryBn", "summaryEn", "impactBn", "impactEn", "whatToDoBn", "whatToDoEn", "patchScheduledAt" }
                .Select(k => item[k]?.ToJsonString() ?? "null")
                .Append(item["advisories"]?.ToJsonString() ?? "[]");
            map[item["id"]!.GetValue<int>()] = string.Join("\u001f", parts);
        }
        return map;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
