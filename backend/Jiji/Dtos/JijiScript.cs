// Jiji — the script JSON (docs/JIJI_PLAN.md, Script JSON) and the requests that carry it.
//
// The stored ScriptJson is always this shape re-serialised: unknown fields are
// dropped, strings trimmed, beats renumbered 1..n. Nothing the model wrote is
// stored as it came.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace RabbitIC.Api.Jiji.Dtos;

public static class JijiVisual
{
    public const string Text = "text";
    public const string Code = "code";
    public const string List = "list";

    public static readonly string[] All = { Text, Code, List };
}

public sealed class JijiBeat
{
    public int Id { get; set; }
    public string Text { get; set; } = "";

    /// <summary>One of <see cref="JijiVisual"/>.</summary>
    public string Visual { get; set; } = JijiVisual.Text;

    /// <summary>code beats only: at most 4 lines, copied from SourceNotes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }

    /// <summary>list beats only: 2–5 short items shown on screen.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Items { get; set; }
}

public sealed class JijiScript
{
    public string ContentType { get; set; } = "";
    public string Language { get; set; } = "en";
    public string Hook { get; set; } = "";
    public string OriginalAngle { get; set; } = "";
    public List<JijiBeat> Beats { get; set; } = new();
    public string MyTake { get; set; } = "";
    public string Cta { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Hashtags { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // Lists and nested objects stay strict about types; a number where text belongs is malformed.
        NumberHandling = JsonNumberHandling.Strict,
    };

    /// <summary>The script, or null with the reason when the JSON is malformed or the wrong shape.</summary>
    public static JijiScript? TryParse(JsonElement element, out string? error)
    {
        error = null;
        if (element.ValueKind != JsonValueKind.Object) { error = "The script must be a JSON object."; return null; }
        try
        {
            var s = element.Deserialize<JijiScript>(Json);
            if (s is null) { error = "The script is empty."; return null; }
            s.Beats ??= new();
            s.Hashtags ??= new();
            if (s.Beats.Any(b => b is null)) { error = "A beat is null."; return null; }
            return s;
        }
        catch (JsonException e)
        {
            error = "Malformed script JSON" + (e.Path is { Length: > 0 } p ? $" at {p}" : "") + ".";
            return null;
        }
    }

    public static JijiScript? TryParse(string json, out string? error)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return TryParse(doc.RootElement, out error);
        }
        catch (JsonException) { error = "Malformed script JSON."; return null; }
    }

    public string Serialize() => JsonSerializer.Serialize(this, Json);
}

public sealed record JijiScriptStats(int Words, int EstimatedSeconds);

/// <summary>What the checks found. Errors block the save (or the approval); warnings never do.</summary>
public sealed record JijiScriptCheck(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, JijiScriptStats Stats)
{
    public bool Ok => Errors.Count == 0;
}

// ---- Requests ----

/// <summary>POST /ideas. Model and source are optional: Jiji:Model / Jiji:ModelSource otherwise.</summary>
public sealed record JijiIdeaRequest(string? ContentType, string? Subject, string? SourceNotes, string? Language, string? Model, string? Source);

public sealed record JijiAngle(string Angle, string Hook, string Why);

public sealed record JijiIdeasDto(IReadOnlyList<JijiAngle> Angles, string Model);

/// <summary>POST /contents: the item, once an angle is picked (or written by hand).</summary>
public sealed record JijiCreateContentRequest(string? ContentType, string? Subject, string? SourceNotes, string? Language, string? Angle);

/// <summary>PUT /contents/{id}: subject, angle and notes, before the script is approved.</summary>
public sealed record JijiUpdateContentRequest(string? Subject, string? SourceNotes, string? Angle, DateTime ExpectedUpdatedAt);

public sealed record JijiGenerateScriptRequest(string? Model, string? Source, DateTime ExpectedUpdatedAt);

public sealed record JijiSaveScriptRequest(JsonElement Script, DateTime ExpectedUpdatedAt);

public sealed record JijiApproveScriptRequest(DateTime ExpectedUpdatedAt);

/// <summary>A write's answer: the item as it now is, and what the checks said.</summary>
public sealed record JijiScriptSaved(JijiContentDto Content, JijiScriptCheck Check);
