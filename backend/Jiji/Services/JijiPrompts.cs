// Jiji — the prompts. Both build only from the item's SourceNotes; the "Jiji voice"
// context source, when the user has one, is a style guide and never a source of facts.

using RabbitIC.Api.Jiji.Models;

namespace RabbitIC.Api.Jiji.Services;

public static class JijiPrompts
{
    /// <summary>The context source (Sources tab) whose text is the writing style. Matched by name, active or not.</summary>
    public const string VoiceSourceName = "Jiji voice";

    public static string DescribeType(string contentType) => contentType switch
    {
        JijiContentType.BuildLog => "Build log: something the author built or a bug they fixed, told as a short story with the lesson at the end.",
        JijiContentType.BookInsight => "Book insight: one idea from a book, credited by title and author, applied to software. Never quote more than one short sentence.",
        JijiContentType.OriginalThought => "Original thought: the author's own argument or opinion about software, with the reasoning behind it.",
        _ => "Tech explainer: one concept explained through a concrete case, not a definition.",
    };

    private const string Channel =
        "You write for a YouTube Shorts channel about practical software engineering and applied AI. " +
        "A working engineer makes it from their own notes and projects. Shorts are 35 to 55 seconds, spoken in plain English.";

    private const string NoInvention =
        "- Build only from the notes. Do not invent facts, numbers, tools, versions, incidents or experiences that are not in them.\n" +
        "- Never claim something was tested, measured or benchmarked unless the notes say so.\n" +
        "- Never mention an employer, a customer, a credential or private infrastructure.\n";

    public static string IdeasSystem(string? voice) =>
        Channel + "\n\n" +
        "Propose 5 distinct angles for one Short about the subject. An angle is the one specific idea the video teaches " +
        "that a generic explainer would not.\n\nRules:\n" + NoInvention +
        "- The 5 angles differ in what they teach, not in wording.\n" +
        "- Prefer angles anchored in a concrete detail from the notes: a bug, a decision, a snippet, a trade-off.\n" +
        "- hook: the first spoken line, at most 14 words, no clickbait, no emoji, no question you do not answer.\n" +
        "- why: one sentence naming the part of the notes that supports the angle.\n\n" +
        Voice(voice) +
        "Return JSON only, no prose, in exactly this shape:\n" +
        "{\"angles\":[{\"angle\":\"\",\"hook\":\"\",\"why\":\"\"}]}";

    public static string IdeasUser(string contentType, string subject, string notes) =>
        $"Content type: {DescribeType(contentType)}\nSubject: {subject}\n\nNotes:\n<<<\n{notes}\n>>>";

    public static string ScriptSystem(string? voice) =>
        Channel + "\n\n" +
        "Write the script for one Short.\n\nRules:\n" + NoInvention +
        "- 90 to 130 spoken words in total across hook, every beat's text, myTake and cta.\n" +
        "- 3 to 6 beats. Each beat's text is one or two short spoken sentences.\n" +
        "- visual is \"text\" by default. Use \"list\" with \"items\" (2 to 4 phrases of at most 5 words) for a set of options or steps. " +
        "Use \"code\" with \"code\" only for a snippet that appears in the notes: copy at most 4 lines exactly, character for character. " +
        "If the notes contain no code, never use \"code\". Never write new code.\n" +
        "- originalAngle: one sentence, the idea this video teaches that a generic explainer would not. Build it from the chosen angle.\n" +
        "- myTake: the author's own real example, in first person, ONLY if the notes contain one; paraphrase it faithfully. " +
        "If the notes contain no first-hand example, return \"\" so the author writes it. Never invent an experience.\n" +
        "- cta: one short line, for example \"Follow for practical software engineering.\"\n" +
        "- title: at most 70 characters, plain, no clickbait, no #, no < or >.\n" +
        "- description: 1 to 3 plain sentences. No links, no hashtags.\n" +
        "- hashtags: 3 to 6 lowercase words, letters and digits only, without #.\n" +
        "- Every field is plain text: no markdown, no emoji, no curly-brace formatting.\n\n" +
        Voice(voice) +
        "Return JSON only, no prose, in exactly this shape:\n" +
        "{\"hook\":\"\",\"originalAngle\":\"\",\"beats\":[{\"id\":1,\"text\":\"\",\"visual\":\"text\"}],\"myTake\":\"\",\"cta\":\"\"," +
        "\"title\":\"\",\"description\":\"\",\"hashtags\":[\"\"]}";

    public static string ScriptUser(JijiContent c) =>
        $"Content type: {DescribeType(c.ContentType)}\nSubject: {c.Subject}\n" +
        $"Chosen angle: {(string.IsNullOrWhiteSpace(c.Angle) ? "(none: pick the strongest one the notes support)" : c.Angle)}\n\n" +
        $"Notes:\n<<<\n{c.SourceNotes}\n>>>";

    public static string Repair(IEnumerable<string> problems) =>
        "That reply cannot be used:\n- " + string.Join("\n- ", problems) +
        "\nReturn the corrected JSON only, in the same shape, following every rule.";

    private static string Voice(string? voice) =>
        string.IsNullOrWhiteSpace(voice) ? ""
            : "The author's writing style (style only, never a source of facts):\n<<<\n" + voice.Trim() + "\n>>>\n\n";
}
