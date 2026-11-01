// Jiji — IdeaGenerator (5 angles from a subject and notes) and ScriptGenerator (the
// script JSON from the item's notes and chosen angle). Each asks once and, when the
// reply is malformed or breaks a rule, once more with the problems listed. A reply
// that still fails is refused: nothing malformed is ever saved.

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Jiji.Dtos;
using RabbitIC.Api.Jiji.Models;
using RabbitIC.Api.ModelSources;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Jiji.Services;

internal static class JijiVoice
{
    public static Task<string?> LoadAsync(AppDbContext db, int ownerId, CancellationToken ct) =>
        db.ContextSources.AsNoTracking()
            .Where(s => s.OwnerUserId == ownerId && s.Name.ToLower() == JijiPrompts.VoiceSourceName.ToLower())
            .OrderBy(s => s.Id)
            .Select(s => s.Content)
            .FirstOrDefaultAsync(ct);
}

public sealed class IdeaGenerator(AppDbContext db, JijiLlm llm)
{
    public const int Count = 5;

    public async Task<JijiResult<JijiIdeasDto>> GenerateAsync(User user, JijiIdeaRequest req, CancellationToken ct)
    {
        var type = JijiContentType.All.FirstOrDefault(t => t.Equals(req.ContentType?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (type is null) return JijiResult<JijiIdeasDto>.Fail(JijiResultKind.Invalid, "contentType is TechExplainer, BuildLog, BookInsight or OriginalThought.");
        if (string.IsNullOrWhiteSpace(req.Subject)) return JijiResult<JijiIdeasDto>.Fail(JijiResultKind.Invalid, "Write the subject.");
        if (string.IsNullOrWhiteSpace(req.SourceNotes)) return JijiResult<JijiIdeasDto>.Fail(JijiResultKind.Invalid, "Paste your notes: angles are built only from them.");

        var (choice, refusal, error) = await llm.ChooseAsync(user, req.Model, req.Source, ct);
        if (choice is null) return JijiResult<JijiIdeasDto>.Fail(refusal, error!);

        var voice = await JijiVoice.LoadAsync(db, user.Id, ct);
        var turns = new List<ChatTurn>
        {
            new("system", JijiPrompts.IdeasSystem(voice)),
            new("user", JijiPrompts.IdeasUser(type, req.Subject.Trim(), req.SourceNotes.Trim())),
        };

        List<string> problems = new();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string reply;
            try { reply = await llm.AskAsync(choice, turns, temperature: 0.8, ct); }
            catch (ChatSourceException e) { return JijiResult<JijiIdeasDto>.Fail(JijiResultKind.Upstream, e.Message); }

            var angles = Read(reply, out problems);
            if (angles is not null) return JijiResult<JijiIdeasDto>.Ok(new JijiIdeasDto(angles, choice.Model));
            turns.Add(new("assistant", reply));
            turns.Add(new("user", JijiPrompts.Repair(problems)));
        }
        return JijiResult<JijiIdeasDto>.Fail(JijiResultKind.Upstream, $"{choice.Model} did not return usable angles.", problems);
    }

    /// <summary>The angles, or null with the problems. Fewer than 3 usable angles is a failure; more than 5 are cut.</summary>
    private static List<JijiAngle>? Read(string reply, out List<string> problems)
    {
        problems = new();
        if (JijiLlm.ExtractObject(reply) is not { } root || !root.TryGetProperty("angles", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            problems.Add("The reply is not a JSON object with an \"angles\" array.");
            return null;
        }
        var angles = arr.EnumerateArray()
            .Where(a => a.ValueKind == JsonValueKind.Object)
            .Select(a => new JijiAngle(Str(a, "angle"), Str(a, "hook"), Str(a, "why")))
            .Where(a => a.Angle != "")
            .DistinctBy(a => a.Angle, StringComparer.OrdinalIgnoreCase)
            .Take(Count)
            .ToList();
        if (angles.Count >= 3) return angles;
        problems.Add($"Return exactly {Count} distinct angles, each with a non-empty \"angle\"; got {angles.Count}.");
        return null;
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";
}

public sealed class ScriptGenerator(AppDbContext db, JijiLlm llm)
{
    public sealed record Generated(JijiScript Script, JijiScriptCheck Check, string Model);

    public async Task<JijiResult<Generated>> GenerateAsync(User user, JijiContent c, string? model, string? source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.SourceNotes))
            return JijiResult<Generated>.Fail(JijiResultKind.Invalid, "Add your notes first: the script is built only from them.");

        var (choice, refusal, error) = await llm.ChooseAsync(user, model, source, ct);
        if (choice is null) return JijiResult<Generated>.Fail(refusal, error!);

        var voice = await JijiVoice.LoadAsync(db, user.Id, ct);
        var turns = new List<ChatTurn>
        {
            new("system", JijiPrompts.ScriptSystem(voice)),
            new("user", JijiPrompts.ScriptUser(c)),
        };

        List<string> problems = new();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string reply;
            try { reply = await llm.AskAsync(choice, turns, temperature: 0.4, ct); }
            catch (ChatSourceException e) { return JijiResult<Generated>.Fail(JijiResultKind.Upstream, e.Message); }

            problems = new();
            JijiScript? script = null;
            if (JijiLlm.ExtractObject(reply) is not { } root) problems.Add("The reply is not one JSON object.");
            else if ((script = JijiScript.TryParse(root, out var parseError)) is null) problems.Add(parseError!);

            if (script is not null)
            {
                JijiScriptValidator.Normalize(script, c);
                var check = JijiScriptValidator.Check(script, c, forApproval: false);
                problems.AddRange(check.Errors);
                if (script.Hook == "") problems.Add("hook is empty.");
                if (script.Beats.Count == 0) problems.Add("beats is empty.");
                if (problems.Count == 0) return JijiResult<Generated>.Ok(new Generated(script, check, choice.Model));
            }
            turns.Add(new("assistant", reply));
            turns.Add(new("user", JijiPrompts.Repair(problems)));
        }
        return JijiResult<Generated>.Fail(JijiResultKind.Upstream, $"{choice.Model} did not return a usable script; nothing was saved.", problems);
    }
}
