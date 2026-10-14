// Assembles the extraction prompt from a buyer profile, the document text and
// worked examples. PURE: no database, no HTTP, no clock — the same inputs always
// give byte-identical messages, which is what makes PromptHash meaningful and
// the builder testable without a model.
//
// The budget is the part that matters. The old fallback cut the document at
// 60,000 characters and told nobody, so a long PO lost its end silently and the
// extraction looked merely "wrong" rather than "incomplete". Here the document
// is never cut: the optional parts are dropped in a fixed order, and if the
// document alone still does not fit, the run FAILS with the numbers.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RabbitIC.Api.Extraction;

/// <summary>One chat message — the shape Ollama's /api/chat takes.</summary>
public sealed record PoPromptMessage(string Role, string Content);

/// <summary>A worked example: the PO's text and the JSON a person confirmed for it.</summary>
public sealed record PoWorkedExample(int RunId, string DocumentText, string ConfirmedJson);

/// <summary>
/// Everything the profile contributes to a prompt, lifted out of the entity so
/// the builder stays pure and the eval can hand it values directly.
/// </summary>
public sealed record PoPromptProfile(
    string? BuyerName,
    string Rules,
    string Glossary,
    int Version,
    IReadOnlyList<PoWorkedExample> Examples);

/// <summary>What the builder did, so the UI and the eval can see the budget's decisions.</summary>
public sealed record PoPromptReport(
    int WindowTokens,
    int ReservedTokens,
    int DocumentTokens,
    int SystemTokens,
    int TotalTokens,
    IReadOnlyList<string> Dropped,
    string? Error);

public sealed record PoPrompt(
    IReadOnlyList<PoPromptMessage> Messages,
    string SystemPrompt,
    string PromptHash,
    PoPromptReport Report)
{
    public bool Ok => Report.Error is null;
}

public static class PoPromptBuilder
{
    /// <summary>Tokens kept clear for the model's JSON answer (§3.3).</summary>
    public const int OutputReserve = 4096;

    /// <summary>The window when Extraction:NumCtx is not configured (§3.3).</summary>
    public const int DefaultNumCtx = 16384;

    /// <summary>A worked example's PO text is cut to this many tokens (§3.3).</summary>
    private const int ExampleTextTokens = 1500;

    /// <summary>
    /// The base rules, carried over verbatim from LlmPoFallback so this build does
    /// not quietly change what the model is told about POs.
    /// </summary>
    public const string BaseRules = """
        You are extracting a garment purchase order. Follow these rules exactly.

        1. Output ONLY JSON matching the given schema. No prose, no markdown fences.
        2. Copy values verbatim from the document. Never compute, infer, or invent a
           number. If a value is absent, use null.
        3. Style number and UPC/GTIN are SEPARATE table columns. Cell text may be
           concatenated with no space (e.g. "MS6SX117RN1400110380823"): the style
           number is the alphanumeric code, and the UPC/GTIN is the trailing 12-13
           digit numeric run. Split them; never merge.
        4. Colour and size are separate fields ("BLUE TXT" is a colour, not part of a
           code). Preserve internal spaces in descriptions.
        5. If the order is a PREPACK/assortment, the header quantity counts PACKS, not
           garments. Put the pack count in orderLine.quantity, set isPrepack true, and
           list each size row in prepackComponents with its own qtyPerPrepack. Do NOT
           collapse sizes to 1 each; a size may repeat within a pack.
        6. Every line item in the document must appear. Do not summarise or truncate.
        """;

    /// <summary>
    /// Ollama's `format`: a real JSON schema object, not the string "json". Derived
    /// from ExtractionResult minus Derived and Validation — those are computed by
    /// the validator, and asking the model for them invites it to invent them.
    /// </summary>
    public static JsonElement OutputSchema { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "object",
          "properties": {
            "header": {
              "type": "object",
              "properties": {
                "buyer": { "type": ["string", "null"] },
                "orderNumber": { "type": ["string", "null"] },
                "poType": { "type": ["string", "null"] },
                "poDate": { "type": ["string", "null"] },
                "cancelDate": { "type": ["string", "null"] },
                "deliveryWindow": {
                  "type": ["object", "null"],
                  "properties": {
                    "start": { "type": ["string", "null"] },
                    "end": { "type": ["string", "null"] }
                  }
                },
                "shippingWindow": {
                  "type": ["object", "null"],
                  "properties": {
                    "start": { "type": ["string", "null"] },
                    "end": { "type": ["string", "null"] }
                  }
                },
                "exitFactoryEndDate": { "type": ["string", "null"] },
                "incoterms": { "type": ["string", "null"] },
                "vendor": {
                  "type": ["object", "null"],
                  "properties": {
                    "locationId": { "type": ["string", "null"] },
                    "name": { "type": ["string", "null"] },
                    "country": { "type": ["string", "null"] }
                  }
                },
                "manufacturingPlant": {
                  "type": ["object", "null"],
                  "properties": {
                    "locationId": { "type": ["string", "null"] },
                    "name": { "type": ["string", "null"] },
                    "country": { "type": ["string", "null"] }
                  }
                }
              }
            },
            "orderLine": {
              "type": "object",
              "properties": {
                "styleNumber": { "type": ["string", "null"] },
                "sku": { "type": ["string", "null"] },
                "upc": { "type": ["string", "null"] },
                "storeNumber": { "type": ["string", "null"] },
                "unitPrice": { "type": ["number", "null"] },
                "quantity": { "type": ["number", "null"] },
                "itemTotal": { "type": ["number", "null"] },
                "uom": { "type": ["string", "null"] },
                "isPrepack": { "type": "boolean" }
              }
            },
            "prepackComponents": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "line": { "type": ["number", "null"] },
                  "sku": { "type": ["string", "null"] },
                  "styleNumber": { "type": ["string", "null"] },
                  "upc": { "type": ["string", "null"] },
                  "productDescription": { "type": ["string", "null"] },
                  "color": { "type": ["string", "null"] },
                  "colorCode": { "type": ["string", "null"] },
                  "size": { "type": ["string", "null"] },
                  "sizeCode": { "type": ["string", "null"] },
                  "qtyPerPrepack": { "type": ["number", "null"] },
                  "unitCost": { "type": ["number", "null"] },
                  "retailPrice": { "type": ["number", "null"] },
                  "uom": { "type": ["string", "null"] }
                }
              }
            },
            "purchaseOrderTotal": { "type": ["number", "null"] }
          },
          "required": ["header", "orderLine", "prepackComponents"]
        }
        """);

    private static readonly string SchemaText =
        JsonSerializer.Serialize(OutputSchema, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// §3.3's estimate: ceil(ASCII / 3.5) + one per non-ASCII character. Deliberately
    /// crude and deliberately pessimistic on non-Latin text — a budget that
    /// under-counts would reintroduce the silent truncation this replaces.
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int ascii = 0, other = 0;
        foreach (var c in text) { if (c < 128) ascii++; else other++; }
        return (int)Math.Ceiling(ascii / 3.5) + other;
    }

    /// <summary>Cut a worked example's text to a token budget. Examples may be cut; the document may not.</summary>
    private static string CutToTokens(string text, int maxTokens)
    {
        if (EstimateTokens(text) <= maxTokens) return text;
        // Binary search on length — the estimate is monotonic in the prefix length.
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (EstimateTokens(text[..mid]) <= maxTokens) lo = mid; else hi = mid - 1;
        }
        return text[..lo];
    }

    /// <summary>
    /// Assemble the messages for one extraction.
    /// </summary>
    /// <param name="numCtx">The window, i.e. Extraction:NumCtx.</param>
    /// <returns>
    /// A prompt whose Report.Error is set when the document alone cannot fit. The
    /// caller must treat that as a failed run and show the message — never cut.
    /// </returns>
    public static PoPrompt Build(
        PoPromptProfile profile, string documentText, int numCtx = DefaultNumCtx)
    {
        documentText ??= "";
        var window = numCtx - OutputReserve;
        var docTokens = EstimateTokens(documentText);
        var dropped = new List<string>();

        // Which optional parts survive. Dropped in the §3.3 order: example 2,
        // then example 1, then the glossary. The document is never a candidate.
        var examples = profile.Examples?.Take(2).ToList() ?? new List<PoWorkedExample>();
        var keepExamples = examples.Count;
        var keepGlossary = !string.IsNullOrWhiteSpace(profile.Glossary);

        string Assemble(int exampleCount, bool glossary)
        {
            var sb = new StringBuilder();
            sb.Append(BaseRules);

            if (!string.IsNullOrWhiteSpace(profile.Rules))
                sb.Append("\n\n## Buyer rules\n").Append(profile.Rules.Trim());

            if (glossary)
                sb.Append("\n\n## Glossary\n").Append(profile.Glossary.Trim());

            sb.Append("\n\n## Output schema\n").Append(SchemaText);

            for (var i = 0; i < exampleCount; i++)
            {
                var ex = examples[i];
                sb.Append($"\n\n## Worked example {i + 1}\n")
                  .Append(CutToTokens(ex.DocumentText ?? "", ExampleTextTokens))
                  .Append("\n\n→\n\n")
                  .Append(ex.ConfirmedJson ?? "");
            }
            return sb.ToString();
        }

        // The document is the floor: if it cannot fit even with nothing optional,
        // the run fails with the numbers rather than losing its end.
        var floorSystem = Assemble(0, false);
        if (EstimateTokens(floorSystem) + docTokens > window)
        {
            var report = new PoPromptReport(
                numCtx, OutputReserve, docTokens, EstimateTokens(floorSystem),
                EstimateTokens(floorSystem) + docTokens,
                new[] { "example2", "example1", "glossary" },
                $"Document is {docTokens} tokens; the window holds {window}. " +
                "Split it or raise Extraction:NumCtx.");
            return new PoPrompt(Array.Empty<PoPromptMessage>(), "", "", report);
        }

        // Drop in order until it fits.
        while (true)
        {
            var system = Assemble(keepExamples, keepGlossary);
            if (EstimateTokens(system) + docTokens <= window) break;

            if (keepExamples == 2) { keepExamples = 1; dropped.Add("example2"); continue; }
            if (keepExamples == 1) { keepExamples = 0; dropped.Add("example1"); continue; }
            if (keepGlossary) { keepGlossary = false; dropped.Add("glossary"); continue; }
            break; // unreachable: the floor already fit.
        }

        var systemPrompt = Assemble(keepExamples, keepGlossary);
        var systemTokens = EstimateTokens(systemPrompt);
        var messages = new[]
        {
            new PoPromptMessage("system", systemPrompt),
            new PoPromptMessage("user", documentText),
        };

        return new PoPrompt(messages, systemPrompt, Sha256(systemPrompt), new PoPromptReport(
            numCtx, OutputReserve, docTokens, systemTokens, systemTokens + docTokens, dropped, null));
    }

    /// <summary>
    /// The repair round (§3.4): the same prompt, plus the model's answer and the
    /// checks it failed, asking for corrected JSON only.
    /// </summary>
    public static IReadOnlyList<PoPromptMessage> BuildRepair(
        PoPrompt original, string modelJson, IEnumerable<string> failedChecks)
    {
        var checks = string.Join("\n", failedChecks.Select(c => "- " + c));
        return new[]
        {
            original.Messages[0],
            original.Messages[1],
            new PoPromptMessage("assistant", modelJson),
            new PoPromptMessage("user",
                "These checks failed on the JSON above:\n" + checks +
                "\n\nReturn the corrected JSON only, in the same schema. " +
                "Fix only what the checks name; copy every other value through unchanged. " +
                "Do not invent numbers — if the document does not state a value, use null."),
        };
    }

    /// <summary>
    /// The system prompt WITHOUT worked examples — what a training line carries
    /// (§3.7), so the exported dataset does not teach the model to expect examples.
    /// </summary>
    public static string SystemPromptWithoutExamples(PoPromptProfile profile)
        => Build(profile with { Examples = Array.Empty<PoWorkedExample>() }, "", int.MaxValue / 2)
           .SystemPrompt;

    private static string Sha256(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
