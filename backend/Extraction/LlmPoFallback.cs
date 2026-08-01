// Unknown-template path. Receives full page text (never chunks — chunk
// boundaries split line-item tables) and asks Ollama for schema-shaped JSON at
// temperature 0. The result is mapped defensively — models emit numbers as
// strings, invent nulls, drop arrays — and then the dispatcher runs the SAME
// validation gate the deterministic parsers get.

using System.Text.Json;
using RabbitIC.Api.AuthHelper;

namespace RabbitIC.Api.Extraction;

public sealed class LlmPoFallback(IHttpClientFactory http, IConfiguration config)
{
    private const string Schema = """
        {
          "header": {
            "buyer": string|null, "orderNumber": string|null, "poType": string|null,
            "poDate": "YYYY-MM-DD"|null, "cancelDate": "YYYY-MM-DD"|null,
            "deliveryWindow": { "start": "YYYY-MM-DD"|null, "end": "YYYY-MM-DD"|null } | null,
            "shippingWindow": { "start": "YYYY-MM-DD"|null, "end": "YYYY-MM-DD"|null } | null,
            "exitFactoryEndDate": "YYYY-MM-DD"|null, "incoterms": string|null,
            "vendor": { "locationId": string|null, "name": string|null, "country": string|null } | null,
            "manufacturingPlant": { "locationId": string|null, "name": string|null, "country": string|null } | null
          },
          "orderLine": {
            "styleNumber": string|null, "sku": string|null, "upc": string|null,
            "storeNumber": string|null, "unitPrice": number|null, "quantity": number|null,
            "itemTotal": number|null, "uom": string|null, "isPrepack": boolean
          },
          "prepackComponents": [ {
            "line": number, "sku": string|null, "styleNumber": string|null, "upc": string|null,
            "productDescription": string|null, "color": string|null, "colorCode": string|null,
            "size": string|null, "sizeCode": string|null, "qtyPerPrepack": number,
            "unitCost": number, "retailPrice": number, "uom": string|null
          } ],
          "purchaseOrderTotal": number|null
        }
        """;

    private const string Rules = """
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
    /// Raised when the model call fails in a way the user can act on (timeout,
    /// Ollama unreachable, model not pulled). Distinct from returning null, which
    /// means "the model answered, but not with usable JSON".
    /// </summary>
    public sealed class ModelUnavailableException(string message) : Exception(message);

    /// <summary>Returns the mapped result WITHOUT validation applied — the dispatcher gates it.</summary>
    public async Task<ExtractionResult?> ExtractAsync(string documentText, string? model, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(documentText)) return null;
        if (documentText.Length > 60_000) documentText = documentText[..60_000];

        var chosenModel = model
            ?? config["Ollama:ExtractionModel"]
            ?? config["Ollama:DefaultModel"]
            ?? "llama3";

        var payload = new
        {
            model = chosenModel,
            stream = false,
            format = "json",
            options = new { temperature = 0 },
            messages = new object[]
            {
                new { role = "system", content = Rules + "\n\nSchema:\n" + Schema },
                new { role = "user",   content = "Extract this purchase order:\n\n" + documentText },
            },
        };

        try
        {
            var client = http.CreateClient("ollama");
            var resp = await client.PostAsJsonAsync("/api/chat", payload, ct);

            // Ollama reports "model not found" as 404 with a JSON body — surface
            // that verbatim rather than as a bare status code, because the fix
            // (`ollama pull <model>`) is only obvious once you can see the name.
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(CancellationToken.None);
                throw new ModelUnavailableException(
                    $"Ollama returned {(int)resp.StatusCode} for model '{chosenModel}'. " +
                    Authorization.Truncate(body, 300));
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var content = doc.RootElement.GetProperty("message").GetProperty("content").GetString();
            return string.IsNullOrWhiteSpace(content) ? null : Map(content);
        }
        // HttpClient.Timeout elapsing throws TaskCanceledException — which derives
        // from OperationCanceledException and so looks exactly like the caller
        // giving up. The caller's token is the only reliable way to tell them
        // apart: if it is NOT signalled, this was our timeout, not their abort.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModelUnavailableException(
                $"The model '{chosenModel}' did not respond within the {TimeoutMinutes}-minute limit. " +
                "It may still be loading, or the document may be too large for it — " +
                $"try a smaller/faster model, or pre-load it with `ollama run {chosenModel}`.");
        }
        catch (HttpRequestException ex)
        {
            throw new ModelUnavailableException($"Could not reach Ollama: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ModelUnavailableException)
        {
            return null;   // answered, but not with usable JSON
        }
    }

    /// <summary>Mirrors the "ollama" handler's configured timeout, for the message above only.</summary>
    private const int TimeoutMinutes = 10;

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
