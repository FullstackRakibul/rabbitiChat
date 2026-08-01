namespace RabbitIC.Api.Extraction;

/// <summary>
/// Derives totals from the component rows, then cross-checks them against the
/// numbers printed on the PO. Both the deterministic parsers and the model
/// fallback run this, so neither can bypass the arithmetic.
///
/// The checks are deliberately redundant: a wrong quantity that survives one
/// check usually breaks another. Anything unverifiable is reported as a failed
/// check rather than silently skipped — "we could not confirm this" and "this is
/// correct" must never look the same to a reviewer.
/// </summary>
public static class ExtractionValidator
{
    /// <summary>Money tolerance. Rounding on a printed PO is a cent at most.</summary>
    private const decimal MoneyEpsilon = 0.01m;

    public static void ApplyDerivedAndValidation(ExtractionResult r)
    {
        Derive(r);
        Validate(r);
    }

    private static void Derive(ExtractionResult r)
    {
        var comps = r.PrepackComponents;
        if (comps.Count > 0)
        {
            r.Derived.PiecesPerPrepack = comps.Sum(c => c.QtyPerPrepack);

            // Unit price of one pack = the pack's contents priced individually.
            r.Derived.ComputedUnitPrice = comps.Sum(c => c.QtyPerPrepack * c.UnitCost);

            if (r.OrderLine.Quantity is int packs)
            {
                r.Derived.TotalPrepacks = packs;
                r.Derived.TotalPieces = packs * r.Derived.PiecesPerPrepack;
            }

            // Per-size totals for the review screen. Group rather than list rows
            // verbatim: a size that repeats within a pack shows one summed line.
            r.Derived.SizeBreakdown = comps
                .GroupBy(c => c.Size ?? "?")
                .Select(g => new SizeCount(
                    g.Key,
                    g.Sum(c => c.QtyPerPrepack),
                    g.Sum(c => c.QtyPerPrepack) * (r.OrderLine.Quantity ?? 0)))
                .ToList();
        }
        else if (r.OrderLine.Quantity is int qty)
        {
            // Non-prepack: the order line is already in pieces.
            r.Derived.TotalPieces = qty;
        }

        var effectiveUnit = r.Derived.ComputedUnitPrice ?? r.OrderLine.UnitPrice;
        if (effectiveUnit is decimal unit && r.OrderLine.Quantity is int q)
            r.Derived.ComputedItemTotal = decimal.Round(unit * q, 2);
    }

    private static void Validate(ExtractionResult r)
    {
        var v = r.Validation;
        v.Clear();

        // ---- identity ----
        var style = r.OrderLine.StyleNumber;
        var upc = r.OrderLine.Upc;

        v.Add(Check("style_number_present", !string.IsNullOrWhiteSpace(style),
            style is null ? "No style number was extracted." : $"Style number: {style}"));

        // The signature failure of flattened text: two adjacent cells merged.
        // A style number that ends in a long digit run has swallowed the UPC.
        v.Add(Check("style_number_not_merged_with_upc",
            style is null || !LooksMerged(style),
            style is null
                ? "No style number to check."
                : LooksMerged(style)
                    ? $"'{style}' looks like a style number concatenated with a UPC — table columns were flattened."
                    : $"'{style}' is a clean style number."));

        v.Add(Check("upc_present_and_numeric",
            upc is not null && IsUpc(upc),
            upc is null ? "No UPC/GTIN was extracted."
                        : IsUpc(upc) ? $"UPC {upc} is a valid 12–14 digit code."
                                     : $"'{upc}' is not a 12–14 digit UPC/GTIN."));

        // ---- prepack structure ----
        if (r.OrderLine.IsPrepack)
        {
            v.Add(Check("prepack_has_components", r.PrepackComponents.Count > 0,
                r.PrepackComponents.Count > 0
                    ? $"{r.PrepackComponents.Count} size rows found."
                    : "Marked as a prepack but no size-breakdown rows were extracted."));

            var missingUpc = r.PrepackComponents.Count(c => string.IsNullOrWhiteSpace(c.Upc));
            v.Add(Check("prepack_components_have_upc", missingUpc == 0,
                missingUpc == 0
                    ? "Every size row carries a UPC."
                    : $"{missingUpc} of {r.PrepackComponents.Count} size rows are missing a UPC."));

            var zeroQty = r.PrepackComponents.Count(c => c.QtyPerPrepack <= 0);
            v.Add(Check("prepack_quantities_positive", zeroQty == 0,
                zeroQty == 0
                    ? "Every size row has a positive quantity."
                    : $"{zeroQty} size rows have a zero or negative quantity."));
        }

        // ---- arithmetic reconciliation ----
        // Pack contents priced individually must equal the printed unit price.
        if (r.Derived.ComputedUnitPrice is decimal computedUnit && r.OrderLine.UnitPrice is decimal printedUnit)
        {
            var ok = Math.Abs(computedUnit - printedUnit) <= MoneyEpsilon;
            v.Add(Check("unit_price_reconciles", ok,
                ok ? $"Pack contents sum to {computedUnit:0.00}, matching the printed unit price."
                   : $"Pack contents sum to {computedUnit:0.00} but the PO prints {printedUnit:0.00}."));
        }
        else if (r.OrderLine.IsPrepack)
        {
            v.Add(Check("unit_price_reconciles", false,
                "Could not reconcile the unit price — component costs or the printed unit price are missing."));
        }

        // quantity x unit price must equal the printed line total.
        if (r.Derived.ComputedItemTotal is decimal computedTotal && r.OrderLine.ItemTotal is decimal printedTotal)
        {
            var ok = Math.Abs(computedTotal - printedTotal) <= MoneyEpsilon;
            v.Add(Check("item_total_reconciles", ok,
                ok ? $"Quantity x unit price = {computedTotal:0.00}, matching the printed line total."
                   : $"Quantity x unit price = {computedTotal:0.00} but the PO prints {printedTotal:0.00}."));
        }
        else
        {
            v.Add(Check("item_total_reconciles", false,
                "Could not reconcile the line total — quantity, unit price or the printed total is missing."));
        }

        // The line total must also agree with the PO-level total.
        if (r.PurchaseOrderTotal is decimal poTotal && r.OrderLine.ItemTotal is decimal itemTotal)
        {
            var ok = Math.Abs(poTotal - itemTotal) <= MoneyEpsilon;
            v.Add(Check("po_total_matches_line_total", ok,
                ok ? $"PO total {poTotal:0.00} matches the line total."
                   : $"PO total {poTotal:0.00} differs from the line total {itemTotal:0.00} " +
                     "(expected on a multi-line PO; verify if this is single-line)."));
        }

        v.Add(Check("total_pieces_derived", r.Derived.TotalPieces is > 0,
            r.Derived.TotalPieces is int tp and > 0
                ? $"{tp} total pieces."
                : "Total pieces could not be derived."));
    }

    private static ValidationCheck Check(string name, bool passed, string detail) => new(name, passed, detail);

    /// <summary>
    /// True when a style number ends in a run of 12+ digits — the fingerprint of a
    /// UPC concatenated onto it by coordinate-blind text extraction.
    /// </summary>
    private static bool LooksMerged(string style)
    {
        int trailingDigits = 0;
        for (int i = style.Length - 1; i >= 0 && char.IsDigit(style[i]); i--) trailingDigits++;
        return trailingDigits >= 12;
    }

    private static bool IsUpc(string s)
    {
        var t = s.Trim();
        return t.Length is >= 12 and <= 14 && t.All(char.IsDigit);
    }
}
