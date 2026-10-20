// Unknown-template path. Receives full page text (never chunks — chunk
// boundaries split line-item tables) and asks Ollama for schema-shaped JSON at
// temperature 0. The result is mapped defensively — models emit numbers as
// strings, invent nulls, drop arrays — and then the dispatcher runs the SAME
// validation gate the deterministic parsers get.
//
// Three things changed when PO Studio arrived:
//   · the prompt comes from PoPromptBuilder, so the buyer's own rules, glossary
//     and worked examples reach the model and the budget is explicit;
//   · `format` carries the JSON schema OBJECT rather than the string "json",
//     with thinking disabled, so the model cannot wander off the shape;
//   · a long document FAILS with its size instead of being cut at 60,000
//     characters, which used to lose the end of a PO without telling anyone.
//
// A repair round lives here too (§3.4): one more request carrying the failed
// checks, and its answer is kept only if it fails FEWER of them.

using System.Text;
using System.Text.Json;
using RabbitIC.Api.ModelSources;

namespace RabbitIC.Api.Extraction;

/// <summary>What the model path produced, with the bookkeeping a run has to record.</summary>
public sealed record LlmExtraction(
    ExtractionResult? Result,
    ExtractionResult? FirstResult,
    bool RepairUsed,
    string Model,
    string PromptHash,
    int? ProfileVersion,
    int PromptTokens,
    int OutputTokens,
    int DurationMs,
    string? Error);

public sealed class LlmPoFallback(IHttpClientFactory http, IConfiguration config)
{
    /// <summary>The context window the budget is computed against.</summary>
    private int NumCtx =>
        int.TryParse(config["Extraction:NumCtx"], out var n) && n > 0 ? n : PoPromptBuilder.DefaultNumCtx;

    /// <summary>The model used when the buyer profile names none.</summary>
    private string DefaultModel =>
        Blank(config["Extraction:DefaultModel"])
        ?? Blank(config["Ollama:ExtractionModel"])
        ?? Blank(config["Ollama:DefaultModel"])
        ?? "gemma4:12b";

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// The dispatcher's existing call shape: no profile knowledge, no repair round.
    /// Everything PO Studio needs goes through the overload below.
    /// </summary>
    public async Task<ExtractionResult?> ExtractAsync(string documentText, string? model, CancellationToken ct)
    {
        var outcome = await ExtractAsync(
            documentText,
            new PoPromptProfile(null, "", "", 1, Array.Empty<PoWorkedExample>()),
            model, validate: null, ct);
        return outcome.Result;
    }

    /// <summary>
    /// The PO Studio path: profile-assembled prompt, schema-forced output, and one
    /// repair round when <paramref name="validate"/> reports failed checks.
    /// </summary>
    /// <param name="validate">
    /// Runs the validation gate on a candidate and returns the checks it failed.
    /// Null skips the repair round.
    /// </param>
    public async Task<LlmExtraction> ExtractAsync(
        string documentText,
        PoPromptProfile profile,
        string? model,
        Func<ExtractionResult, IReadOnlyList<string>>? validate,
        CancellationToken ct)
    {
        var chosenModel = string.IsNullOrWhiteSpace(model) ? DefaultModel : model!;
        var started = System.Diagnostics.Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(documentText))
            return Failed(chosenModel, "", profile.Version, started, "The document has no text to extract from.");

        // The budget decides; it never cuts. An oversized document fails here with
        // its size, which is the whole point of replacing the 60,000-char cut.
        var prompt = PoPromptBuilder.Build(profile, documentText, NumCtx);
        if (!prompt.Ok)
            return Failed(chosenModel, prompt.PromptHash, profile.Version, started, prompt.Report.Error);

        var first = await CallAsync(chosenModel, prompt.Messages, ct);
        if (first.Content is null)
            return Failed(chosenModel, prompt.PromptHash, profile.Version, started,
                          first.Error ?? "The model did not return usable JSON.");

        var firstResult = Map(first.Content);
        if (firstResult is null)
            return Failed(chosenModel, prompt.PromptHash, profile.Version, started,
                          "The model did not return usable JSON.");

        var promptTokens = first.PromptTokens;
        var outputTokens = first.OutputTokens;

        // ---- repair round (§3.4) ----
        // Only when a check actually failed, and only once. The second answer has
        // to fail FEWER checks to be kept: a model that "fixes" one number by
        // breaking two has not improved anything.
        var repairUsed = false;
        var kept = firstResult;

        if (validate is not null)
        {
            var firstFailed = validate(firstResult);
            if (firstFailed.Count > 0)
            {
                var repairMessages = PoPromptBuilder.BuildRepair(prompt, first.Content, firstFailed);
                var second = await CallAsync(chosenModel, repairMessages, ct);
                var secondResult = second.Content is null ? null : Map(second.Content);

                if (secondResult is not null)
                {
                    promptTokens += second.PromptTokens;
                    outputTokens += second.OutputTokens;
                    if (validate(secondResult).Count < firstFailed.Count)
                    {
                        kept = secondResult;
                        repairUsed = true;
                    }
                }
            }
        }

        started.Stop();
        return new LlmExtraction(
            kept, firstResult, repairUsed, chosenModel, prompt.PromptHash, profile.Version,
            promptTokens, outputTokens, (int)started.ElapsedMilliseconds, null);
    }

    private static LlmExtraction Failed(
        string model, string hash, int version, System.Diagnostics.Stopwatch sw, string? error)
    {
        sw.Stop();
        return new LlmExtraction(null, null, false, model, hash, version, 0, 0,
                                 (int)sw.ElapsedMilliseconds, error);
    }

    /// <summary>
    /// One /api/chat call with the PO schema as `format` — through the shared
    /// SchemaJsonChat, which Matrix's mapping suggestions use too.
    /// </summary>
    private Task<SchemaJsonAnswer> CallAsync(
        string model, IReadOnlyList<PoPromptMessage> messages, CancellationToken ct) =>
        SchemaJsonChat.CallAsync(http, model, PoPromptBuilder.OutputSchema,
            messages.Select(m => new SchemaJsonMessage(m.Role, m.Content)), NumCtx, ct);

    private static ExtractionResult? Map(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var root = d.RootElement;
            var r = new ExtractionResult();

            if (root.TryGetProperty("header", out var h) && h.ValueKind == JsonValueKind.Object)
            {
                r.Header.Buyer = Str(h, "buyer");
                r.Header.OrderNumber = Str(h, "orderNumber");
                r.Header.PoType = Str(h, "poType");
                r.Header.PoDate = Date(Str(h, "poDate"));
                r.Header.CancelDate = Date(Str(h, "cancelDate"));
                r.Header.DeliveryWindow = Window(h, "deliveryWindow");
                r.Header.ShippingWindow = Window(h, "shippingWindow");
                r.Header.ExitFactoryEndDate = Date(Str(h, "exitFactoryEndDate"));
                r.Header.Incoterms = Str(h, "incoterms");
                r.Header.Vendor = Party(h, "vendor");
                r.Header.ManufacturingPlant = Party(h, "manufacturingPlant");
            }
            if (root.TryGetProperty("orderLine", out var o) && o.ValueKind == JsonValueKind.Object)
            {
                r.OrderLine.StyleNumber = Str(o, "styleNumber");
                r.OrderLine.Sku = Str(o, "sku");
                r.OrderLine.Upc = Str(o, "upc");
                r.OrderLine.StoreNumber = Str(o, "storeNumber");
                r.OrderLine.UnitPrice = Dec(o, "unitPrice");
                r.OrderLine.Quantity = Int(o, "quantity");
                r.OrderLine.ItemTotal = Dec(o, "itemTotal");
                r.OrderLine.Uom = Str(o, "uom");
                r.OrderLine.IsPrepack = o.TryGetProperty("isPrepack", out var ip)
                                        && ip.ValueKind == JsonValueKind.True;
            }
            if (root.TryGetProperty("prepackComponents", out var comps)
                && comps.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in comps.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) continue;
                    r.PrepackComponents.Add(new PrepackComponent
                    {
                        Line = Int(c, "line") ?? r.PrepackComponents.Count + 1,
                        Sku = Str(c, "sku"),
                        StyleNumber = Str(c, "styleNumber"),
                        Upc = Str(c, "upc"),
                        ProductDescription = Str(c, "productDescription"),
                        Color = Str(c, "color"),
                        ColorCode = Str(c, "colorCode"),
                        Size = Str(c, "size"),
                        SizeCode = Str(c, "sizeCode"),
                        QtyPerPrepack = Int(c, "qtyPerPrepack") ?? 0,
                        UnitCost = Dec(c, "unitCost") ?? 0,
                        RetailPrice = Dec(c, "retailPrice") ?? 0,
                        Uom = Str(c, "uom"),
                    });
                }
            }
            r.PurchaseOrderTotal = Dec(root, "purchaseOrderTotal");
            return r;
        }
        catch
        {
            return null;
        }
    }

    // Models emit "12.5" as often as 12.5 — accept both.
    private static string? Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? Dec(JsonElement e, string p)
    {
        if (!e.TryGetProperty(p, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(v.GetString(), out var d) => d,
            _ => null,
        };
    }

    private static int? Int(JsonElement e, string p) => (int?)Dec(e, p);

    private static DateOnly? Date(string? s) => DateOnly.TryParse(s, out var d) ? d : null;

    private static DateWindow? Window(JsonElement e, string p)
    {
        if (!e.TryGetProperty(p, out var v) || v.ValueKind != JsonValueKind.Object) return null;
        var w = new DateWindow(Date(Str(v, "start")), Date(Str(v, "end")));
        return w.Start is null && w.End is null ? null : w;
    }

    private static PartyRef? Party(JsonElement e, string p)
    {
        if (!e.TryGetProperty(p, out var v) || v.ValueKind != JsonValueKind.Object) return null;
        var party = new PartyRef { LocationId = Str(v, "locationId"), Name = Str(v, "name"), Country = Str(v, "country") };
        return party.LocationId is null && party.Name is null && party.Country is null ? null : party;
    }
}
