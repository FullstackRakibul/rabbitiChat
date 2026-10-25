// সত্য মন্ত্রণালয় — fix guides (docs/TRUTH_PROMPT.md §6).
//
// A guide is templated steps for each platform and version. The variant grid
// renders every platform × variable combination; the safety check runs on each;
// and a person publishes (§6.3) only when, since the last edit, they opened the
// grid, nothing is block-level, every warning is acknowledged with a note, and
// the 4-item checklist is ticked. Any edit resets the grid and the checklist.
//
// Publishing writes an immutable snapshot through an allow-list (BuildSnapshot);
// the public reads only snapshots of published guides. Acknowledgement notes and
// user ids stay on this side; the warnings themselves go out, for readers.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.DTOs.Truth;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Truth;

/// <summary>One rendered variant: its values, its visible steps and their findings.</summary>
public sealed record GuideVariant(string Key, Dictionary<string, string> Values, List<RenderedGuideStep> Steps, List<SafetyFinding> Findings, List<string> Errors);

public sealed record RenderedGuideStep(int Order, string Kind, string Platform, string Code, string? Expected, string? Language);

public sealed partial class GuideService(AppDbContext db, TruthPlatforms platforms)
{
    public const int MaxVariants = 500;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,59}$")]
    private static partial Regex Slug();

    [GeneratedRegex(@"^[a-zA-Z][\w-]{0,29}$")]
    private static partial Regex VarName();

    private sealed record Checklist(bool Tested, bool Verify, bool Undo, bool Sources, int ByUserId, DateTime AtUtc);

    // ─────────────────────────── reads ───────────────────────────

    public async Task<List<object>> ListAsync(CancellationToken ct)
    {
        var guides = await db.TrGuides.AsNoTracking().OrderByDescending(g => g.EditedAtUtc).ToListAsync(ct);
        var techs = await db.TrTechs.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        return guides.Select(g => (object)new
        {
            g.Id, g.Slug, g.TitleBn, g.TitleEn, g.Phase, g.Status, g.ItemId, tech = techs.GetValueOrDefault(g.TechId),
            platforms = TruthJson.Strings(g.PlatformsJson),
            editedAt = TruthJson.Utc(g.EditedAtUtc), gridOpenedAt = TruthJson.Utc(g.GridOpenedAtUtc), publishedAt = TruthJson.Utc(g.PublishedAtUtc),
            unpublishedChanges = g.Status == TrIssueStatus.Published && g.PublishedAtUtc < g.EditedAtUtc,
        }).ToList();
    }

    public async Task<TrResult<object>> GetAsync(int id, CancellationToken ct)
    {
        var g = await db.TrGuides.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return TrResult<object>.NotFound("No such guide.");
        var steps = await StepsAsync(id, ct);
        var sources = await SourcesAsync(id, ct);
        var acks = await db.TrSafetyFindings.AsNoTracking().Where(f => f.GuideId == id).ToListAsync(ct);
        var corrections = await db.TrGuideCorrections.AsNoTracking().Where(c => c.GuideId == id).OrderBy(c => c.AtUtc).ToListAsync(ct);
        var names = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Username, ct);
        var checklist = ReadChecklist(g);
        return TrResult<object>.Ok(new
        {
            guide = new
            {
                g.Id, g.Slug, g.ItemId, g.TechId, g.TitleBn, g.TitleEn, g.Phase, g.IntroBn, g.IntroEn,
                variables = JsonNode.Parse(g.VariablesJson), platforms = TruthJson.Strings(g.PlatformsJson),
                g.BackupFirst, g.AuthorName, g.BasedOnSourceOrder, g.Status,
                editedAt = TruthJson.Utc(g.EditedAtUtc), gridOpenedAt = TruthJson.Utc(g.GridOpenedAtUtc),
                gridCurrent = GridCurrent(g),
                checklist = checklist is null ? null : new { checklist.Tested, checklist.Verify, checklist.Undo, checklist.Sources, by = names.GetValueOrDefault(checklist.ByUserId), at = TruthJson.Utc(checklist.AtUtc), current = checklist.AtUtc >= g.EditedAtUtc },
                firstPublishedAt = TruthJson.Utc(g.FirstPublishedAtUtc), publishedAt = TruthJson.Utc(g.PublishedAtUtc),
                handVerified = TruthJson.Hand(g.HandVerifiedJson).Select(h => new { h.Token, h.Note, by = names.GetValueOrDefault(h.ByUserId), at = TruthJson.Utc(h.AtUtc) }),
            },
            steps = steps.Select(s => new
            {
                s.Order, s.Kind, s.Platform, s.TitleBn, s.TitleEn, s.BodyBn, s.BodyEn, s.Code, s.Language, s.ExpectedOutput, s.Condition, s.SideEffectsBn, s.SideEffectsEn,
            }),
            sources = sources.Select(s => new { s.Url, s.Publisher, s.Author, s.Title, s.PublishedOn, s.Kind, s.FetchedText }),
            acknowledged = acks.Select(a => new { a.FindingKey, a.RuleId, a.Variant, a.StepOrder, note = a.AcknowledgedNote, by = names.GetValueOrDefault(a.AcknowledgedByUserId), at = TruthJson.Utc(a.AtUtc) }),
            corrections = corrections.Select(c => new { at = TruthJson.Utc(c.AtUtc), c.TextBn, c.TextEn, by = names.GetValueOrDefault(c.ByUserId) }),
        });
    }

    /// <summary>The platforms a guide may declare, with their groups (platforms.json).</summary>
    public object Platforms() => new { groups = platforms.Groups, platforms = platforms.All };

    private Task<List<TrGuideStep>> StepsAsync(int id, CancellationToken ct) =>
        db.TrGuideSteps.AsNoTracking().Where(s => s.GuideId == id).OrderBy(s => s.Order).ToListAsync(ct);

    private Task<List<TrSource>> SourcesAsync(int id, CancellationToken ct) =>
        db.TrSources.AsNoTracking().Where(s => s.GuideId == id).OrderBy(s => s.Order).ToListAsync(ct);

    private static bool GridCurrent(TrGuide g) => g.GridOpenedAtUtc is { } at && at >= g.EditedAtUtc;

    private static Checklist? ReadChecklist(TrGuide g)
    {
        try { return JsonSerializer.Deserialize<Checklist>(g.ChecklistJson, TruthJson.Options) is { ByUserId: > 0 } c ? c : null; }
        catch (JsonException) { return null; }
    }

    // ─────────────────────────── edit ───────────────────────────

    public async Task<TrResult<TrGuide>> SaveAsync(int? id, GuideInput input, User user, CancellationToken ct)
    {
        var slug = input.Slug?.Trim().ToLowerInvariant() ?? "";
        if (!Slug().IsMatch(slug)) return TrResult<TrGuide>.Invalid("The slug is lower-case letters, digits and dashes (up to 60).");
        if (await db.TrGuides.AnyAsync(g => g.Slug == slug && g.Id != (id ?? 0), ct)) return TrResult<TrGuide>.Conflict($"A guide with the slug {slug} exists.");
        if (!await db.TrTechs.AnyAsync(t => t.Id == input.TechId, ct)) return TrResult<TrGuide>.Invalid("Choose a technology.");
        if (input.ItemId is { } item && !await db.TrItems.AnyAsync(i => i.Id == item, ct)) return TrResult<TrGuide>.Invalid("No such item.");
        if (!TrGuidePhase.All.Contains(input.Phase)) return TrResult<TrGuide>.Invalid($"The phase is one of: {string.Join(", ", TrGuidePhase.All)}.");
        var plats = (input.Platforms ?? new()).Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToList();
        if (plats.Count == 0) return TrResult<TrGuide>.Invalid("Choose at least one platform.");
        if (plats.FirstOrDefault(p => platforms.Find(p) is null) is { } unknown) return TrResult<TrGuide>.Invalid($"No platform {unknown} in platforms.json.");
        if (ValidateVariables(input.Variables) is { } varError) return TrResult<TrGuide>.Invalid(varError);
        var steps = input.Steps ?? new();
        if (steps.Count > 60) return TrResult<TrGuide>.Invalid("Up to 60 steps.");
        foreach (var (s, n) in steps.Select((s, n) => (s, n + 1)))
        {
            if (!TrStepKind.All.Contains(s.Kind)) return TrResult<TrGuide>.Invalid($"Step {n}: the kind is one of {string.Join(", ", TrStepKind.All)}.");
            var platform = string.IsNullOrWhiteSpace(s.Platform) ? "any" : s.Platform.Trim();
            if (platform != "any" && !plats.Contains(platform)) return TrResult<TrGuide>.Invalid($"Step {n} is for {platform}, which the guide doesn't declare.");
            if ((s.Code?.Length ?? 0) > 20_000 || (s.ExpectedOutput?.Length ?? 0) > 10_000) return TrResult<TrGuide>.Invalid($"Step {n} is too long.");
        }
        foreach (var src in input.Sources ?? new())
        {
            if (!Uri.TryCreate(src.Url?.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return TrResult<TrGuide>.Invalid($"A source link must be https: {src.Url}");
            if (!TrSourceKind.All.Contains(src.Kind)) return TrResult<TrGuide>.Invalid($"A source kind is one of: {string.Join(", ", TrSourceKind.All)}.");
        }
        if (input.BasedOnSourceOrder is { } b && (b < 1 || b > (input.Sources?.Count ?? 0))) return TrResult<TrGuide>.Invalid("“Based on” must be one of the guide's sources.");

        var now = DateTime.UtcNow;
        TrGuide g;
        if (id is null) db.TrGuides.Add(g = new TrGuide { CreatedByUserId = user.Id, CreatedAtUtc = now });
        else
        {
            g = (await db.TrGuides.FirstOrDefaultAsync(x => x.Id == id, ct))!;
            if (g is null) return TrResult<TrGuide>.NotFound("No such guide.");
            if (g.FirstPublishedAtUtc is not null && g.Slug != slug) return TrResult<TrGuide>.Invalid("This guide has been published, so its slug is in shared links and cannot change.");
        }
        g.Slug = slug; g.ItemId = input.ItemId; g.TechId = input.TechId; g.Phase = input.Phase;
        g.TitleBn = input.TitleBn?.Trim() ?? ""; g.TitleEn = Trim(input.TitleEn);
        g.IntroBn = Trim(input.IntroBn); g.IntroEn = Trim(input.IntroEn);
        g.VariablesJson = (input.Variables ?? new JsonObject()).ToJsonString();
        g.PlatformsJson = TruthJson.FromStrings(plats);
        g.BackupFirst = input.BackupFirst; g.AuthorName = input.AuthorName?.Trim() ?? ""; g.BasedOnSourceOrder = input.BasedOnSourceOrder;
        // Any edit: the grid must be opened again, and the checklist ticked again (§6.3, G2.4).
        g.EditedAtUtc = now; g.GridOpenedAtUtc = null; g.ChecklistJson = "{}";
        await db.SaveChangesAsync(ct);

        db.TrGuideSteps.RemoveRange(db.TrGuideSteps.Where(s => s.GuideId == g.Id));
        db.TrSources.RemoveRange(db.TrSources.Where(s => s.GuideId == g.Id));
        var order = 0;
        foreach (var s in steps)
            db.TrGuideSteps.Add(new TrGuideStep
            {
                GuideId = g.Id, Order = ++order, Kind = s.Kind, Platform = string.IsNullOrWhiteSpace(s.Platform) ? "any" : s.Platform.Trim(),
                TitleBn = Trim(s.TitleBn), TitleEn = Trim(s.TitleEn), BodyBn = Trim(s.BodyBn), BodyEn = Trim(s.BodyEn),
                Code = s.Code?.Replace("\r\n", "\n").TrimEnd(), Language = Trim(s.Language), ExpectedOutput = s.ExpectedOutput?.Replace("\r\n", "\n").TrimEnd(),
                Condition = Trim(s.Condition), SideEffectsBn = Trim(s.SideEffectsBn), SideEffectsEn = Trim(s.SideEffectsEn),
            });
        order = 0;
        foreach (var s in input.Sources ?? new())
            db.TrSources.Add(new TrSource
            {
                GuideId = g.Id, Order = order++, Url = s.Url.Trim(), Publisher = s.Publisher?.Trim() ?? "", Author = Trim(s.Author), Title = s.Title?.Trim() ?? "",
                PublishedOn = Trim(s.PublishedOn), Kind = s.Kind, FetchedText = s.FetchedText ?? "", FetchedAtUtc = string.IsNullOrEmpty(s.FetchedText) ? null : now,
            });
        await db.SaveChangesAsync(ct);
        return TrResult<TrGuide>.Ok(g);
    }

    private static string? ValidateVariables(JsonObject? vars)
    {
        foreach (var (name, node) in vars ?? new JsonObject())
        {
            if (!VarName().IsMatch(name) || name == "platform") return $"\"{name}\" can't be a variable name (letters, digits, - and _; not \"platform\", which is the platform selector).";
            if (node is not JsonObject v || v["options"] is not JsonArray opts || opts.Count is 0 or > 20 || opts.Any(o => o is not JsonValue ov || !ov.TryGetValue<string>(out _)))
                return $"\"{name}\" needs 1–20 options, as text.";
            var list = opts.Select(o => o!.GetValue<string>()).ToList();
            if (v["default"]?.GetValue<string>() is { } d && !list.Contains(d)) return $"\"{name}\"'s default is not one of its options.";
        }
        return null;
    }

    public async Task<TrResult<bool>> DeleteAsync(int id, CancellationToken ct)
    {
        var g = await db.TrGuides.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return TrResult<bool>.NotFound("No such guide.");
        if (g.FirstPublishedAtUtc is not null) return TrResult<bool>.Invalid("This guide has been published. Withdraw it instead; what was published is kept.");
        db.TrGuideSteps.RemoveRange(db.TrGuideSteps.Where(s => s.GuideId == id));
        db.TrSources.RemoveRange(db.TrSources.Where(s => s.GuideId == id));
        db.TrSafetyFindings.RemoveRange(db.TrSafetyFindings.Where(s => s.GuideId == id));
        db.TrGuides.Remove(g);
        await db.SaveChangesAsync(ct);
        return TrResult<bool>.Ok(true);
    }

    // ─────────────────────────── the variant grid ───────────────────────────

    /// <summary>Every platform × every combination of the variables, platform first, in declared order.</summary>
    public static List<Dictionary<string, string>> Variants(TrGuide g)
    {
        var plats = TruthJson.Strings(g.PlatformsJson);
        var vars = (JsonNode.Parse(g.VariablesJson) as JsonObject ?? new JsonObject())
            .Select(kv => (kv.Key, Options: kv.Value?["options"]?.AsArray().Select(o => o!.GetValue<string>()).ToList() ?? new List<string>()))
            .ToList();
        var combos = new List<Dictionary<string, string>>();
        foreach (var p in plats) combos.Add(new Dictionary<string, string> { ["platform"] = p });
        foreach (var (name, options) in vars)
            combos = combos.SelectMany(c => options.Select(o => new Dictionary<string, string>(c) { [name] = o })).Take(MaxVariants + 1).ToList();
        return combos;
    }

    /// <summary>The variant a page opens on: the first platform and every variable's default.</summary>
    public static Dictionary<string, string> DefaultVariant(TrGuide g)
    {
        var v = new Dictionary<string, string> { ["platform"] = TruthJson.Strings(g.PlatformsJson).FirstOrDefault() ?? "any" };
        foreach (var (name, node) in JsonNode.Parse(g.VariablesJson) as JsonObject ?? new JsonObject())
            v[name] = node?["default"]?.GetValue<string>() ?? node?["options"]?.AsArray().FirstOrDefault()?.GetValue<string>() ?? "";
        return v;
    }

    public static string VariantKey(IReadOnlyDictionary<string, string> values) => string.Join(";", values.Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>A variant's visible steps, rendered, with the safety check's findings and any template errors.</summary>
    public GuideVariant Render(IReadOnlyList<TrGuideStep> steps, Dictionary<string, string> values)
    {
        var errors = new List<string>();
        var visible = new List<RenderedGuideStep>();
        foreach (var s in steps)
        {
            if (s.Platform != "any" && s.Platform != values.GetValueOrDefault("platform")) continue;
            if (!string.IsNullOrWhiteSpace(s.Condition))
            {
                var condErrors = new List<string>();
                var show = GuideTemplate.EvalCondition(s.Condition, values, condErrors);
                errors.AddRange(condErrors.Select(e => $"step {s.Order} condition: {e}"));
                if (!show) continue;
            }
            var code = GuideTemplate.Render(s.Code ?? "", values);
            var expected = s.ExpectedOutput is null ? null : GuideTemplate.Render(s.ExpectedOutput, values);
            errors.AddRange(code.Errors.Select(e => $"step {s.Order}: {e}"));
            if (expected is not null) errors.AddRange(expected.Errors.Select(e => $"step {s.Order} expected output: {e}"));
            var language = s.Language ?? platforms.Find(values.GetValueOrDefault("platform") ?? "")?.Shell;
            visible.Add(new RenderedGuideStep(s.Order, s.Kind, s.Platform, code.Text, expected?.Text, language));
        }
        var findings = SafetyCheck.Run(visible.Select(v => new RenderedStep(v.Order, v.Kind, v.Code, v.Language)).ToList(), values, platforms.Directives);
        return new GuideVariant(VariantKey(values), values, visible, findings, errors.Distinct().ToList());
    }

    private async Task<(TrGuide Guide, List<GuideVariant> Variants)?> GridOfAsync(int id, CancellationToken ct)
    {
        var g = await db.TrGuides.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return null;
        var steps = await StepsAsync(id, ct);
        return (g, Variants(g).Take(MaxVariants).Select(v => Render(steps, v)).ToList());
    }

    /// <summary>The whole grid — and the record that the publisher opened it (§6.3).</summary>
    public async Task<TrResult<object>> GridAsync(int id, CancellationToken ct)
    {
        var grid = await GridOfAsync(id, ct);
        if (grid is null) return TrResult<object>.NotFound("No such guide.");
        var (g, variants) = grid.Value;
        var acks = await db.TrSafetyFindings.AsNoTracking().Where(f => f.GuideId == id).ToDictionaryAsync(f => f.FindingKey, ct);
        var names = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Username, ct);
        var tracked = await db.TrGuides.FirstAsync(x => x.Id == id, ct);
        tracked.GridOpenedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        var all = variants.SelectMany(v => v.Findings.Select(f => (v, f, key: SafetyCheck.Key(f, v.Key)))).ToList();
        return TrResult<object>.Ok(new
        {
            total = Variants(g).Count,
            capped = Variants(g).Count > MaxVariants,
            summary = new
            {
                variants = variants.Count,
                blocks = all.Count(x => x.f.Level == SafetyFinding.Block),
                warnings = all.Count(x => x.f.Level == SafetyFinding.Warn),
                unacknowledged = all.Count(x => x.f.Level == SafetyFinding.Warn && !acks.ContainsKey(x.key)),
                templateErrors = variants.Sum(v => v.Errors.Count),
            },
            variants = variants.Select(v => new
            {
                v.Key, v.Values, v.Errors,
                steps = v.Steps.Select(s => new { s.Order, s.Kind, s.Platform, s.Code, s.Expected, s.Language }),
                findings = v.Findings.Select(f =>
                {
                    var key = SafetyCheck.Key(f, v.Key);
                    var ack = acks.GetValueOrDefault(key);
                    return new
                    {
                        key, f.RuleId, f.Level, f.StepOrder, f.Line, f.Message,
                        acknowledged = ack is null ? null : new { note = ack.AcknowledgedNote, by = names.GetValueOrDefault(ack.AcknowledgedByUserId), at = TruthJson.Utc(ack.AtUtc) },
                    };
                }),
            }),
        });
    }

    /// <summary>Acknowledges one warning, with a note. A block can't be acknowledged: it must be fixed.</summary>
    public async Task<TrResult<bool>> AcknowledgeAsync(int id, AckInput input, User user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Note) || input.Note.Trim().Length < 5) return TrResult<bool>.Invalid("Say why it is acceptable (at least 5 characters).");
        var grid = await GridOfAsync(id, ct);
        if (grid is null) return TrResult<bool>.NotFound("No such guide.");
        var match = grid.Value.Variants.SelectMany(v => v.Findings.Select(f => (v, f))).FirstOrDefault(x => SafetyCheck.Key(x.f, x.v.Key) == input.FindingKey);
        if (match.f is null) return TrResult<bool>.Invalid("No such finding in the current variants (the guide may have changed).");
        if (match.f.Level == SafetyFinding.Block) return TrResult<bool>.Invalid("A block-level finding can't be acknowledged; change the step.");
        var row = await db.TrSafetyFindings.FirstOrDefaultAsync(f => f.GuideId == id && f.FindingKey == input.FindingKey, ct);
        if (row is null) db.TrSafetyFindings.Add(row = new TrSafetyFinding { GuideId = id, FindingKey = input.FindingKey });
        row.StepOrder = match.f.StepOrder; row.Variant = match.v.Key; row.RuleId = match.f.RuleId; row.Level = match.f.Level; row.Message = match.f.Message;
        row.AcknowledgedByUserId = user.Id; row.AcknowledgedNote = input.Note.Trim(); row.AtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return TrResult<bool>.Ok(true);
    }

    public async Task<TrResult<bool>> SetChecklistAsync(int id, ChecklistInput input, User user, CancellationToken ct)
    {
        var g = await db.TrGuides.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return TrResult<bool>.NotFound("No such guide.");
        g.ChecklistJson = JsonSerializer.Serialize(new Checklist(input.Tested, input.Verify, input.Undo, input.Sources, user.Id, DateTime.UtcNow), TruthJson.Options);
        await db.SaveChangesAsync(ct);
        return TrResult<bool>.Ok(true);
    }

    public async Task<TrResult<int>> VerifyNumberAsync(int id, VerifyInput input, User user, CancellationToken ct)
    {
        var g = await db.TrGuides.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return TrResult<int>.NotFound("No such guide.");
        if (string.IsNullOrWhiteSpace(input.Note) || input.Note.Trim().Length < 5) return TrResult<int>.Invalid("Say how it was verified (at least 5 characters).");
        if (!(await NumbersAsync(g, ct)).Any(f => f.Token == input.Token)) return TrResult<int>.Invalid("That number is not in this guide.");
        var list = TruthJson.Hand(g.HandVerifiedJson).Where(h => h.Token != input.Token).ToList();
        list.Add(new TruthJson.HandVerified(input.Token, input.Note.Trim(), user.Id, DateTime.UtcNow));
        g.HandVerifiedJson = TruthJson.FromHand(list);
        await db.SaveChangesAsync(ct);
        return TrResult<int>.Ok(id);
    }

    public async Task<TrResult<int>> AddCorrectionAsync(int id, CorrectionInput input, User user, CancellationToken ct)
    {
        if (!await db.TrGuides.AnyAsync(g => g.Id == id, ct)) return TrResult<int>.NotFound("No such guide.");
        if (string.IsNullOrWhiteSpace(input.TextBn)) return TrResult<int>.Invalid("A correction needs its Bangla text.");
        db.TrGuideCorrections.Add(new TrGuideCorrection { GuideId = id, AtUtc = DateTime.UtcNow, TextBn = input.TextBn.Trim(), TextEn = Trim(input.TextEn), ByUserId = user.Id });
        await db.SaveChangesAsync(ct);
        return TrResult<int>.Ok(id);
    }

    // ─────────────────────────── the checks before publishing ───────────────────────────

    private static IEnumerable<(string Field, string Text)> GuideTexts(TrGuide g, IReadOnlyList<TrGuideStep> steps)
    {
        (string, string?)[] own = { ("titleBn", g.TitleBn), ("titleEn", g.TitleEn), ("introBn", g.IntroBn), ("introEn", g.IntroEn) };
        foreach (var (f, t) in own) if (!string.IsNullOrWhiteSpace(t)) yield return (f, t!);
        foreach (var s in steps)
        foreach (var (f, t) in new[] { ("titleBn", s.TitleBn), ("titleEn", s.TitleEn), ("bodyBn", s.BodyBn), ("bodyEn", s.BodyEn), ("sideEffectsBn", s.SideEffectsBn), ("sideEffectsEn", s.SideEffectsEn) })
            if (!string.IsNullOrWhiteSpace(t)) yield return ($"steps[{s.Order}].{f}", t!);
    }

    private async Task<List<NumberFinding>> NumbersAsync(TrGuide g, CancellationToken ct)
    {
        var steps = await StepsAsync(g.Id, ct);
        var sources = await SourcesAsync(g.Id, ct);
        var hand = TruthJson.Hand(g.HandVerifiedJson).GroupBy(h => h.Token).ToDictionary(x => x.Key, x => x.Last().Note);
        return NumberCheck.Run(GuideTexts(g, steps), sources, hand);
    }

    /// <summary>What blocks publishing, and the number check — the editor's checklist panel.</summary>
    public async Task<TrResult<object>> CheckAsync(int id, CancellationToken ct)
    {
        var g = await db.TrGuides.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return TrResult<object>.NotFound("No such guide.");
        var problems = await ProblemsAsync(g, ct);
        return TrResult<object>.Ok(new { ok = problems.Count == 0, problems, numbers = await NumbersAsync(g, ct) });
    }

    private async Task<List<TrProblem>> ProblemsAsync(TrGuide g, CancellationToken ct)
    {
        var problems = new List<TrProblem>();
        void Add(string code, string message, string? field = null) => problems.Add(new TrProblem(null, code, message, field));
        var steps = await StepsAsync(g.Id, ct);
        var sources = await SourcesAsync(g.Id, ct);

        if (sources.Count == 0) Add("noSource", "No source. Every guide is built from at least one public source (rule 1).");
        foreach (var (s, n) in sources.Select((s, n) => (s, n + 1)))
            if (string.IsNullOrWhiteSpace(s.Publisher) || string.IsNullOrWhiteSpace(s.Title)) Add("sourceIncomplete", $"Source [{n}] needs a publisher and a title.");
        if (string.IsNullOrWhiteSpace(g.TitleBn)) Add("bangla", "The Bangla title is empty.", "titleBn");
        if (!string.IsNullOrWhiteSpace(g.IntroEn) && string.IsNullOrWhiteSpace(g.IntroBn)) Add("bangla", "The intro has English but no Bangla.", "introBn");
        foreach (var s in steps)
            foreach (var (bn, en, f) in new[] { (s.TitleBn, s.TitleEn, "title"), (s.BodyBn, s.BodyEn, "body"), (s.SideEffectsBn, s.SideEffectsEn, "what this may break") })
                if (!string.IsNullOrWhiteSpace(en) && string.IsNullOrWhiteSpace(bn)) Add("bangla", $"Step {s.Order}'s {f} has English but no Bangla.");
        if (steps.Count == 0) Add("noSteps", "The guide has no steps.");
        if (steps.Any(s => TrStepKind.Changing.Contains(s.Kind)))
        {
            if (!steps.Any(s => s.Kind == "verify")) Add("noVerify", "The guide changes a server, so it needs a verify step (§8).");
            if (!steps.Any(s => s.Kind == "undo")) Add("noUndo", "The guide changes a server, so it needs an undo step (§8).");
        }
        foreach (var f in (await NumbersAsync(g, ct)).Where(f => !f.Ok))
            Add("numbers", $"{f.Token} ({f.Kind}, in {string.Join(", ", f.Fields)}) is not in any source. Fix it, or verify it by hand with a note.");

        var variants = Variants(g);
        if (variants.Count > MaxVariants) Add("tooManyVariants", $"{variants.Count} variants; the grid checks at most {MaxVariants}. Use fewer options.");
        var rendered = variants.Take(MaxVariants).Select(v => Render(steps, v)).ToList();
        var errs = rendered.SelectMany(v => v.Errors.Select(e => $"{v.Key}: {e}")).Distinct().ToList();
        if (errs.Count > 0) Add("template", $"Template errors: {string.Join(" · ", errs.Take(5))}{(errs.Count > 5 ? $" (+{errs.Count - 5})" : "")}");
        var acks = await db.TrSafetyFindings.AsNoTracking().Where(f => f.GuideId == g.Id).Select(f => f.FindingKey).ToListAsync(ct);
        var findings = rendered.SelectMany(v => v.Findings.Select(f => (v, f))).ToList();
        foreach (var (v, f) in findings.Where(x => x.f.Level == SafetyFinding.Block).DistinctBy(x => (x.f.RuleId, x.f.StepOrder)))
            Add("safetyBlock", $"Step {f.StepOrder}: {f.RuleId} — {f.Message} (e.g. {v.Key})");
        var unacked = findings.Count(x => x.f.Level == SafetyFinding.Warn && !acks.Contains(SafetyCheck.Key(x.f, x.v.Key)));
        if (unacked > 0) Add("safetyWarn", $"{unacked} warning(s) across the variants not acknowledged yet.");

        if (!GridCurrent(g)) Add("gridNotOpened", "Open the variant grid: it hasn't been opened since the last edit (§6.3).");
        var c = ReadChecklist(g);
        if (c is null || c.AtUtc < g.EditedAtUtc || !(c.Tested && c.Verify && c.Undo && c.Sources))
            Add("checklist", "Tick the 4 checks since the last edit: tested on a real or test server, verify step matches, undo step works, sources linked.");

        // Rule 5: a published guide whose words changed needs a visible correction.
        var previous = await db.TrGuideSnapshots.AsNoTracking().Where(s => s.GuideId == g.Id).OrderByDescending(s => s.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (previous is not null && Wording(JsonNode.Parse(previous.Json)!.AsObject()) != Wording(await BuildSnapshotAsync(g, DateTime.UtcNow, ct))
            && !await db.TrGuideCorrections.AnyAsync(x => x.GuideId == g.Id && x.AtUtc > previous.CreatedAtUtc, ct))
            Add("correctionNeeded", "This guide's published words have changed. Add a correction saying what changed (rule 5).");
        return problems;
    }

    // ─────────────────────────── publish ───────────────────────────

    public async Task<TrResult<object>> PublishAsync(int id, User user, CancellationToken ct)
    {
        var g = await db.TrGuides.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return TrResult<object>.NotFound("No such guide.");
        var problems = await ProblemsAsync(g, ct);
        if (problems.Count > 0) return TrResult<object>.Invalid("The guide's checklist is not clean.", problems);
        var now = DateTime.UtcNow;
        var json = (await BuildSnapshotAsync(g, now, ct)).ToJsonString();
        db.TrGuideSnapshots.Add(new TrGuideSnapshot { GuideId = g.Id, GuideSlug = g.Slug, Json = json, ETag = TruthSnapshot.ETag(json), CreatedAtUtc = now, PublishedByUserId = user.Id });
        g.Status = TrIssueStatus.Published;
        g.FirstPublishedAtUtc ??= now;
        g.PublishedAtUtc = now;
        g.PublishedByUserId = user.Id;
        await db.SaveChangesAsync(ct);
        return TrResult<object>.Ok(new { publishedAt = TruthJson.Utc(now) });
    }

    public async Task<TrResult<bool>> WithdrawAsync(int id, CancellationToken ct)
    {
        var g = await db.TrGuides.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return TrResult<bool>.NotFound("No such guide.");
        if (g.Status != TrIssueStatus.Published) return TrResult<bool>.Invalid("The guide is not published.");
        g.Status = TrIssueStatus.Draft;
        await db.SaveChangesAsync(ct);
        return TrResult<bool>.Ok(true);
    }

    /// <summary>
    /// THE guide allow-list. Templates go out as templates (the page renders them for
    /// the reader's platform and version, the same renderer as here); the warnings
    /// someone acknowledged go out by rule and variant, never with the note.
    /// </summary>
    private async Task<JsonObject> BuildSnapshotAsync(TrGuide g, DateTime publishedAt, CancellationToken ct)
    {
        var steps = await StepsAsync(g.Id, ct);
        var sources = await SourcesAsync(g.Id, ct);
        var tech = await db.TrTechs.AsNoTracking().FirstAsync(t => t.Id == g.TechId, ct);
        var corrections = await db.TrGuideCorrections.AsNoTracking().Where(c => c.GuideId == g.Id).OrderBy(c => c.AtUtc).ToListAsync(ct);
        string? issueSlug = null;
        if (g.ItemId is { } itemId && await db.TrItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct) is { } item)
            issueSlug = await db.TrIssues.AsNoTracking().Where(i => i.Id == item.IssueId).Select(i => i.Slug).FirstOrDefaultAsync(ct);
        var warnings = Variants(g).Take(MaxVariants).Select(v => Render(steps, v))
            .SelectMany(v => v.Findings.Where(f => f.Level == SafetyFinding.Warn).Select(f => new JsonObject
            {
                ["variant"] = v.Key, ["step"] = f.StepOrder, ["rule"] = f.RuleId,
            })).ToList();
        var plats = TruthJson.Strings(g.PlatformsJson).Select(platforms.Find).Where(p => p is not null).Select(p => (JsonNode?)new JsonObject
        {
            ["key"] = p!.Key, ["nameBn"] = p.NameBn, ["nameEn"] = p.NameEn, ["shell"] = p.Shell, ["hintBn"] = p.HintBn, ["hintEn"] = p.HintEn,
        }).ToArray();
        string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        return new JsonObject
        {
            ["schema"] = 1,
            ["guide"] = new JsonObject
            {
                ["slug"] = g.Slug, ["phase"] = g.Phase, ["titleBn"] = g.TitleBn, ["titleEn"] = Blank(g.TitleEn),
                ["introBn"] = Blank(g.IntroBn), ["introEn"] = Blank(g.IntroEn), ["backupFirst"] = g.BackupFirst, ["author"] = g.AuthorName,
                ["itemId"] = g.ItemId, ["issueSlug"] = issueSlug,
                ["basedOn"] = g.BasedOnSourceOrder is { } b && b >= 1 && b <= sources.Count ? b : null,
                ["firstPublishedAt"] = TruthJson.Utc(g.FirstPublishedAtUtc ?? publishedAt), ["publishedAt"] = TruthJson.Utc(publishedAt),
            },
            ["tech"] = new JsonObject { ["slug"] = tech.Slug, ["name"] = tech.Name, ["monogram"] = tech.Monogram, ["hue"] = tech.Hue },
            ["platforms"] = new JsonArray(plats),
            ["variables"] = JsonNode.Parse(g.VariablesJson),
            ["steps"] = new JsonArray(steps.Select(s => (JsonNode?)new JsonObject
            {
                ["order"] = s.Order, ["kind"] = s.Kind, ["platform"] = s.Platform,
                ["titleBn"] = Blank(s.TitleBn), ["titleEn"] = Blank(s.TitleEn), ["bodyBn"] = Blank(s.BodyBn), ["bodyEn"] = Blank(s.BodyEn),
                ["code"] = Blank(s.Code), ["language"] = Blank(s.Language), ["expected"] = Blank(s.ExpectedOutput), ["condition"] = Blank(s.Condition),
                ["sideEffectsBn"] = Blank(s.SideEffectsBn), ["sideEffectsEn"] = Blank(s.SideEffectsEn),
            }).ToArray()),
            ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode?)w).ToArray()),
            ["sources"] = new JsonArray(sources.Select((s, i) => (JsonNode?)new JsonObject
            {
                ["n"] = i + 1, ["url"] = s.Url, ["publisher"] = s.Publisher, ["author"] = Blank(s.Author), ["title"] = s.Title, ["publishedOn"] = Blank(s.PublishedOn), ["kind"] = s.Kind,
            }).ToArray()),
            ["corrections"] = new JsonArray(corrections.Select(c => (JsonNode?)new JsonObject { ["at"] = TruthJson.Utc(c.AtUtc), ["textBn"] = c.TextBn, ["textEn"] = Blank(c.TextEn) }).ToArray()),
        };
    }

    /// <summary>A guide's words and steps as one string, for "changed since published" (rule 5).</summary>
    private static string Wording(JsonObject snapshot)
    {
        var guide = snapshot["guide"]!;
        return string.Join("\u001f", new[] { "titleBn", "titleEn", "introBn", "introEn" }.Select(k => guide[k]?.ToJsonString() ?? "null")
            .Append(snapshot["steps"]?.ToJsonString() ?? "[]").Append(snapshot["variables"]?.ToJsonString() ?? "{}"));
    }

    // ─────────────────────────── what the public reads ───────────────────────────

    public async Task<PublicSnapshot?> PublishedSnapshotAsync(string slug, CancellationToken ct)
    {
        var id = await db.TrGuides.AsNoTracking().Where(g => g.Status == TrIssueStatus.Published && g.Slug == slug).Select(g => (int?)g.Id).FirstOrDefaultAsync(ct);
        if (id is null) return null;
        return await db.TrGuideSnapshots.AsNoTracking().Where(s => s.GuideId == id).OrderByDescending(s => s.CreatedAtUtc).ThenByDescending(s => s.Id)
            .Select(s => new PublicSnapshot(s.Json, s.ETag, false)).FirstOrDefaultAsync(ct);
    }

    /// <summary>Every published guide, from its latest snapshot: what the guides list and the item cards' buttons need.</summary>
    public async Task<string> IndexJsonAsync(CancellationToken ct)
    {
        var slugs = await db.TrGuides.AsNoTracking().Where(g => g.Status == TrIssueStatus.Published).OrderByDescending(g => g.PublishedAtUtc).Select(g => g.Slug).ToListAsync(ct);
        var list = new JsonArray();
        foreach (var slug in slugs)
        {
            if (await PublishedSnapshotAsync(slug, ct) is not { } snap) continue;
            var doc = JsonNode.Parse(snap.Json)!;
            var g = doc["guide"]!;
            list.Add(new JsonObject
            {
                ["slug"] = slug, ["phase"] = g["phase"]?.GetValue<string>(), ["titleBn"] = g["titleBn"]?.GetValue<string>(), ["titleEn"] = g["titleEn"]?.GetValue<string>(),
                ["itemId"] = g["itemId"]?.GetValue<int?>(), ["issueSlug"] = g["issueSlug"]?.GetValue<string>(), ["publishedAt"] = g["publishedAt"]?.GetValue<string>(),
                ["tech"] = doc["tech"]!.DeepClone(),
                ["platforms"] = new JsonArray(doc["platforms"]!.AsArray().Select(p => (JsonNode?)new JsonObject
                {
                    ["key"] = p!["key"]!.GetValue<string>(), ["nameBn"] = p["nameBn"]!.GetValue<string>(), ["nameEn"] = p["nameEn"]!.GetValue<string>(),
                }).ToArray()),
            });
        }
        return new JsonObject { ["guides"] = list }.ToJsonString();
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
