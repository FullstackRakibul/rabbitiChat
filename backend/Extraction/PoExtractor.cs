// Deterministic parser for the Kohl's / SPS Commerce purchase-order template.
//
// Why geometry instead of text: PdfPig emits adjacent table cells that touch as
// a SINGLE word crossing the column boundary — the reference file yields
// "MS6SX117RN1400187583462TXT", which is the vendor style, the UPC and the start
// of the colour description with no whitespace anywhere. There is nothing to
// split on, so every field here is read by x-position from column bands rather
// than by parsing the flattened word stream.
//
// Bands are derived per page from the item-table header row, never hardcoded:
// the same PO printed at a different scale still lands its columns correctly,
// and page 2 carries its own header.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace RabbitIC.Api.Extraction;

public sealed partial class PoExtractor : IPoParser
{
    public string Key => "KohlsSps";
    public string DisplayName => "Kohl's (SPS Commerce)";

    // Vertical tolerance when collecting letters onto one row baseline.
    private const double RowTolerance = 3.0;

    // Unit cost and retail price are stacked in one cell, ~3-4pt either side of
    // the row baseline, so the money band needs a taller window than a normal cell.
    private const double MoneyTolerance = 8.0;

    public double Detect(string firstPageText, string? pdfProducer)
    {
        var haystack = (firstPageText + " " + (pdfProducer ?? "")).ToLowerInvariant();
        double score = 0;
        if (haystack.Contains("sps commerce")) score += 0.4;
        if (haystack.Contains("kohls") || haystack.Contains("kohl's")) score += 0.3;
        if (haystack.Contains("upc/gtin")) score += 0.2;
        if (haystack.Contains("vendors style") || haystack.Contains("vendor")) score += 0.1;
        return Math.Min(score, 1.0);
    }

    public ExtractionResult Parse(Stream pdfStream)
    {
        using var pdf = PdfDocument.Open(pdfStream);
        var result = new ExtractionResult();
        var pages = pdf.GetPages().ToList();

        ReadHeader(pages[0], result.Header);

        // Component rows continue across pages; each page re-derives its bands.
        var components = new List<PrepackComponent>();
        OrderLineRaw? parent = null;

        foreach (var page in pages)
        {
            var bands = DeriveBands(page);
            if (bands is null) continue;      // no item table on this page

            parent ??= ReadParentRow(page, bands);
            components.AddRange(ReadComponentRows(page, bands));
        }

        if (parent is null)
            throw new InvalidOperationException("Item-table header row not found — layout has drifted.");
        if (components.Count == 0)
            throw new InvalidOperationException("No prepack component rows were read.");

        // Renumber sequentially across pages: the LINE column restarts per page.
        for (var i = 0; i < components.Count; i++) components[i].Line = i + 1;

        result.OrderLine = new OrderLine
        {
            StyleNumber = parent.StyleNumber,
            Upc = parent.Upc,
            Sku = parent.Sku,
            StoreNumber = parent.StoreNumber,
            UnitPrice = parent.UnitPrice,
            Quantity = parent.Quantity,
            ItemTotal = parent.ItemTotal,
            Uom = parent.Uom,
            IsPrepack = true,
        };
        result.PrepackComponents = components;
        result.Header.Buyer ??= "Kohls";
        result.PurchaseOrderTotal = parent.PurchaseOrderTotal ?? parent.ItemTotal;

        return result;
    }

    // ───────────────────────── document header ─────────────────────────

    /// <summary>
    /// Header fields are label/value pairs rather than a table, so they are read
    /// by locating the label and taking the nearest value to its right or below.
    /// </summary>
    private static void ReadHeader(Page page, PoHeader header)
    {
        var words = page.GetWords().ToList();

        string? ValueRightOf(string label, double maxGap = 220)
        {
            var l = words.FirstOrDefault(w => w.Text.Equals(label, StringComparison.OrdinalIgnoreCase));
            if (l is null) return null;
            return words
                .Where(w => Math.Abs(w.BoundingBox.Top - l.BoundingBox.Top) <= RowTolerance
                            && w.BoundingBox.Left > l.BoundingBox.Right
                            && w.BoundingBox.Left - l.BoundingBox.Right < maxGap)
                .OrderBy(w => w.BoundingBox.Left)
                .FirstOrDefault()?.Text;
        }

        // Values printed under their label (dates in the window boxes).
        string? ValueUnder(params string[] labelWords)
        {
            var anchor = words.FirstOrDefault(w =>
                w.Text.Equals(labelWords[0], StringComparison.OrdinalIgnoreCase));
            if (anchor is null) return null;
            return words
                .Where(w => w.BoundingBox.Top < anchor.BoundingBox.Top
                            && anchor.BoundingBox.Top - w.BoundingBox.Top < 22
                            && Math.Abs(w.BoundingBox.Left - anchor.BoundingBox.Left) < 40)
                .OrderByDescending(w => w.BoundingBox.Top)
                .FirstOrDefault()?.Text;
        }

        header.Buyer = words.Any(w => w.Text.StartsWith("Kohls", StringComparison.OrdinalIgnoreCase))
            ? "Kohls" : null;
        header.OrderNumber = ValueRightOf("#:") ?? ValueRightOf("Order");
        header.PoType = ValueRightOf("Type:");
        header.PoDate = ParseDate(ValueUnder("Date:"));
        header.CancelDate = ParseDate(ValueUnder("Cancel"));

        var delivery = ParseDate(ValueUnder("Delivery"));
        if (delivery is not null) header.DeliveryWindow = new DateWindow(delivery, null);
        var shipping = ParseDate(ValueUnder("Shipping"));
        if (shipping is not null) header.ShippingWindow = new DateWindow(shipping, null);

        header.Vendor = ReadParty(words, "Vendor:");
        header.ManufacturingPlant = ReadParty(words, "Plant");
    }

    private static PartyRef? ReadParty(List<Word> words, string label)
    {
        var anchor = words.FirstOrDefault(w => w.Text.StartsWith(label, StringComparison.OrdinalIgnoreCase));
        if (anchor is null) return null;

        // Location ID and name are printed a few lines below the label.
        var below = words
            .Where(w => w.BoundingBox.Top < anchor.BoundingBox.Top
                        && anchor.BoundingBox.Top - w.BoundingBox.Top < 40
                        && w.BoundingBox.Left >= anchor.BoundingBox.Left - 20)
            .OrderByDescending(w => w.BoundingBox.Top)
            .ToList();

        var id = below.FirstOrDefault(w => w.Text.Length >= 6 && w.Text.All(char.IsDigit))?.Text;
        var name = string.Join(' ', below
            .Where(w => w.Text.Any(char.IsLetter) && !w.Text.EndsWith(':'))
            .Select(w => w.Text)
            .Take(3));

        if (id is null && string.IsNullOrWhiteSpace(name)) return null;
        return new PartyRef { LocationId = id, Name = string.IsNullOrWhiteSpace(name) ? null : name };
    }

    private static DateOnly? ParseDate(string? s) =>
        DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    // ───────────────────────── column bands ─────────────────────────

    /// <summary>One column of the item table: [XLo, XHi).</summary>
    private sealed record Band(double XLo, double XHi);

    private sealed class Bands
    {
        public required Band Line { get; init; }
        public required Band Sku { get; init; }
        public required Band VendorPn { get; init; }
        public required Band Upc { get; init; }
        public required Band Description { get; init; }
        public required Band StoreQty { get; init; }
        public required Band Money { get; init; }
        public required Band Qty { get; init; }
        public required Band Uom { get; init; }
        public required Band ItemTotal { get; init; }

        /// <summary>Vendor PN + UPC read as one span, then split on the digit run.</summary>
        public Band StyleAndUpc => new(VendorPn.XLo, Upc.XHi);

        public double HeaderTop { get; init; }
    }

    /// <summary>
    /// Finds the item-table header row and turns the header words' x0 values into
    /// bands, each running to the next header's x0. Returns null when this page
    /// has no item table.
    /// </summary>
    private static Bands? DeriveBands(Page page)
    {
        var words = page.GetWords().ToList();

        // The header row is identified by UPC/GTIN + ITEMTOTAL sharing a baseline.
        var anchor = words.FirstOrDefault(w =>
            w.Text.Equals("UPC/GTIN", StringComparison.OrdinalIgnoreCase));
        if (anchor is null) return null;

        var top = anchor.BoundingBox.Top;
        var row = words
            .Where(w => Math.Abs(w.BoundingBox.Top - top) <= 10)
            .OrderBy(w => w.BoundingBox.Left)
            .ToList();

        double? X(params string[] names) => row
            .FirstOrDefault(w => names.Any(n => w.Text.Equals(n, StringComparison.OrdinalIgnoreCase)))
            ?.BoundingBox.Left;

        // "DESCRIPTIONLINE" and "COST/RETAIL" arrive as merged header words, and
        // UNIT/PRICE sit on their own baseline — so match by prefix where needed.
        double? XStarts(string prefix) => row
            .FirstOrDefault(w => w.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?.BoundingBox.Left;

        var xLine = X("LINE") ?? XStarts("LINE");
        var xSku = X("SKU");
        var xVendor = X("VENDOR");
        var xUpc = anchor.BoundingBox.Left;
        var xDesc = XStarts("DESCRIPTION") ?? X("ITEM");
        var xItemTotal = XStarts("ITEMTOTAL");
        var xMoney = XStarts("COST/RETAIL") ?? XStarts("UNIT");
        var xUom = X("UOM");

        if (xLine is null || xSku is null || xVendor is null || xDesc is null
            || xItemTotal is null || xMoney is null || xUom is null)
            return null;

        // Cell text does not align with its header word — it can start a couple of
        // points to the LEFT of the header x0 (the UPC's first digit sits at 177.5
        // under a header at 177.8). Nudging each boundary left by a small margin
        // keeps a cell whole instead of shaving its first glyph into the previous
        // column, which is what produced 11-digit UPCs.
        const double Margin = 2.5;

        // Cell text in the vendor-PN column is left of its header word; open the
        // band midway back toward SKU so it captures the cell but not the SKU.
        var xVendorCell = xSku.Value + (xVendor.Value - xSku.Value) / 2;
        xUpc -= Margin;
        xDesc = xDesc.Value - Margin;

        // Three columns are all titled "QTY"; take them left-to-right.
        var qtys = row
            .Where(w => w.Text.Equals("QTY", StringComparison.OrdinalIgnoreCase))
            .Select(w => w.BoundingBox.Left)
            .OrderBy(x => x)
            .ToList();

        // The store/qty group sits between description and the money column; the
        // component qty is the last QTY before UOM.
        var xStoreQty = qtys.FirstOrDefault(x => x > xDesc && x < xMoney.Value);
        var xQty = qtys.LastOrDefault(x => x > xMoney.Value && x < xUom.Value);
        if (xStoreQty == 0 || xQty == 0) return null;

        return new Bands
        {
            Line = new(xLine.Value, xSku.Value),
            Sku = new(xSku.Value, xVendorCell),
            // Cell text starts slightly left of the "VENDOR" header word, so the
            // band opens at the widest point that still excludes the SKU column.
            VendorPn = new(xVendorCell, xUpc),
            Upc = new(xUpc, xDesc.Value),
            Description = new(xDesc.Value, xStoreQty),
            StoreQty = new(xStoreQty, xMoney.Value),
            Money = new(xMoney.Value, xQty - Margin),
            // The UOM cell starts well left of its header ("Each" at 505.9 under a
            // header at 512.9), so the qty band must stop short of it or it reads
            // "1Ea" and the integer parse fails.
            Qty = new(xQty - Margin, xQty + 12),
            Uom = new(xQty + 12, xItemTotal.Value - Margin),
            ItemTotal = new(xItemTotal.Value, page.Width),
            HeaderTop = top,
        };
    }

    // ───────────────────────── cell reading ─────────────────────────

    /// <summary>
    /// Collects individual LETTERS whose glyph falls inside the band and within
    /// yTol of the row baseline, ordered by x. Reading by letter rather than by
    /// word is the whole point: a word that straddles a column boundary is cut
    /// at the boundary instead of landing wholly in one column.
    /// </summary>
    private static string CellText(Page page, double topCenter, double xLo, double xHi, double yTol)
    {
        var sb = new StringBuilder();
        double? lastRight = null;

        foreach (var letter in page.Letters
            .Where(l => Math.Abs(l.BoundingBox.Top - topCenter) <= yTol
                        && l.BoundingBox.Left >= xLo
                        && l.BoundingBox.Left < xHi)
            .OrderBy(l => l.BoundingBox.Left))
        {
            // Reinstate spaces the glyph stream omits, so "BLUE TXT" keeps its gap.
            // The threshold must clear intra-word kerning (this template leaves up
            // to ~4pt between glyphs of one code) or style numbers gain fake spaces.
            if (lastRight is double r && letter.BoundingBox.Left - r > 4.5) sb.Append(' ');
            sb.Append(letter.Value);
            lastRight = letter.BoundingBox.Right;
        }
        return sb.ToString().Trim();
    }

    private static string Cell(Page page, Bands b, double top, Band band) =>
        CellText(page, top, band.XLo, band.XHi, RowTolerance);

    // ───────────────────────── rows ─────────────────────────

    private sealed class OrderLineRaw
    {
        public string? StyleNumber, Upc, Sku, StoreNumber, Uom;
        public decimal? UnitPrice, ItemTotal, PurchaseOrderTotal;
        public int? Quantity;
    }

    /// <summary>
    /// The parent order line: style/UPC from the merged cell, unit price from the
    /// "Unit Price:" cell, and the pack count from the QTY column — never from a
    /// merchandise or item total, which are money and would silently validate.
    /// </summary>
    private OrderLineRaw? ReadParentRow(Page page, Bands b)
    {
        // The parent row is the one carrying "Vendors Style" / the unit price.
        var marker = page.GetWords().FirstOrDefault(w =>
            w.Text.StartsWith("Vendors", StringComparison.OrdinalIgnoreCase));
        if (marker is null) return null;

        var top = marker.BoundingBox.Top;
        var raw = new OrderLineRaw
        {
            // UPC is on the label row, in the UPC band.
            Upc = Cell(page, b, top, b.Upc),
            Sku = Cell(page, b, top, b.Sku),
            StoreNumber = Cell(page, b, top, b.StoreQty),
        };

        // "Vendors Style Number:" wraps onto a second line, with the value in the
        // description band beside it; strip any trailing "-COLOUR" suffix.
        var styleCell = CellText(page, top - 6.7, b.Description.XLo, b.Description.XHi, RowTolerance);
        var dash = styleCell.IndexOf('-');
        // Style codes are printed with literal space glyphs inside them
        // ("MS6SX 117RN1"); strip whitespace so the code matches the components'.
        raw.StyleNumber = (dash > 0 ? styleCell[..dash] : styleCell).Replace(" ", "").Trim();

        // Quantity: the QTY+UOM cell renders merged as "1154Assortment". Read it
        // from the QTY band onward and split leading digits from trailing letters.
        // It must never come from ITEMTOTAL or the merchandise total, which are money.
        var qtyCell = CellText(page, top, b.Qty.XLo, b.ItemTotal.XLo, RowTolerance);
        var qtyMatch = LeadingDigits().Match(qtyCell.Replace(" ", ""));
        if (qtyMatch.Success)
        {
            raw.Quantity = int.Parse(qtyMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            var trailing = qtyMatch.Groups[2].Value.Trim();
            if (trailing.Length > 0) raw.Uom = trailing;
        }

        // Unit price sits under the "Unit Price:" label — the label is on the parent
        // row, the value on the line beneath it — and inside the money band, not in
        // the stacked cost/retail cell the components use.
        raw.UnitPrice = FindDecimalNear(page, top, b.Money.XLo, b.Qty.XLo, RowTolerance)
                     ?? FindDecimalNear(page, top - 6.7, b.Money.XLo, b.Qty.XLo, RowTolerance);

        raw.ItemTotal = ReadRowItemTotal(page, top, b);
        raw.PurchaseOrderTotal = FindMerchandiseTotal(page) ?? raw.ItemTotal;

        return raw;
    }

    private List<PrepackComponent> ReadComponentRows(Page page, Bands b)
    {
        var rows = new List<PrepackComponent>();

        // Every component row is anchored by its style cell in the vendor-PN band.
        // Cluster letter tops rather than grouping on exact values: glyphs on one
        // baseline vary by a fraction of a point, and a stray sub-row would
        // otherwise become a phantom component.
        var tops = page.Letters
            .Where(l => l.BoundingBox.Left >= b.VendorPn.XLo
                        && l.BoundingBox.Left < b.VendorPn.XHi
                        && l.BoundingBox.Top < b.HeaderTop)
            .Select(l => l.BoundingBox.Top)
            .OrderByDescending(t => t)
            .ToList();

        var anchors = new List<double>();
        foreach (var t in tops)
            if (anchors.Count == 0 || Math.Abs(anchors[^1] - t) > RowTolerance * 2)
                anchors.Add(t);

        foreach (var top in anchors)
        {
            // Read the two columns as separate bands — geometry already puts the
            // boundary in the right place, so no string splitting is needed. The
            // regex split is only a fallback for when a glyph straddles the edge.
            var style = Cell(page, b, top, b.VendorPn);
            var upc = Cell(page, b, top, b.Upc);

            if (!IsUpc(upc))
            {
                (style, upc) = SplitStyleAndUpc(
                    CellText(page, top, b.StyleAndUpc.XLo, b.StyleAndUpc.XHi, RowTolerance));
            }

            if (string.IsNullOrEmpty(style) || !IsUpc(upc)) continue;   // not a component row

            // The parent order line also carries a style+UPC in these bands; it is
            // the row holding the "Vendors Style" label, and is not a component.
            if (style.StartsWith("Vendors", StringComparison.OrdinalIgnoreCase)) continue;

            // Guard from the spec: never emit a style that still carries a UPC.
            if (MergedStyle().IsMatch(style))
                throw new InvalidOperationException(
                    $"Style '{style}' still contains a UPC — column split failed.");

            var qtyText = Cell(page, b, top, b.Qty);
            if (!int.TryParse(qtyText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qty))
                qty = 0;

            var (cost, retail) = ReadMoneyPair(page, top, b.Money);

            rows.Add(new PrepackComponent
            {
                StyleNumber = style,
                Upc = upc,
                Sku = Cell(page, b, top, b.Sku),
                Uom = Cell(page, b, top, b.Uom),
                QtyPerPrepack = qty,
                UnitCost = cost ?? 0,
                RetailPrice = retail ?? 0,
                Color = ReadColor(page, top, b),
                Size = ReadSize(page, top, b),
            });
        }

        return rows;
    }

    // ───────────────────────── field helpers ─────────────────────────

    /// <summary>
    /// Geometry isolates the vendor-PN + UPC span; this places the exact boundary
    /// inside it, because a single character can straddle the column edge. The UPC
    /// is the trailing 12-13 digit run; anything before it is the style number.
    /// </summary>
    private static (string? Style, string? Upc) SplitStyleAndUpc(string cell)
    {
        if (string.IsNullOrWhiteSpace(cell)) return (null, null);

        var compact = cell.Replace(" ", "");

        // Prefer a 12-digit UPC over 13: this template prints GTIN-12, and a greedy
        // 13-digit match would silently annex the style's last character. Geometry
        // is authoritative — this split only runs when a glyph straddled the edge.
        var m = StyleUpc12().Match(compact);
        if (!m.Success) m = StyleUpc().Match(compact);
        if (!m.Success) return (compact.Length > 0 ? compact : null, null);

        var style = m.Groups[1].Value;
        var upc = m.Groups[2].Value;

        // A style that still carries a full UPC means the split failed; refuse it
        // rather than emit a merged value the reviewer would have to catch.
        if (MergedStyle().IsMatch(style))
            throw new InvalidOperationException(
                $"Style '{style}' still contains a UPC — column split failed.");

        return (style.Length > 0 ? style : null, upc);
    }

    /// <summary>
    /// Unit cost and retail price share one cell, stacked either side of the row
    /// baseline. Smaller top = cost (printed above), larger top = retail.
    /// </summary>
    private static (decimal? Cost, decimal? Retail) ReadMoneyPair(Page page, double top, Band money)
    {
        var decimals = page.GetWords()
            .Where(w => w.BoundingBox.Left >= money.XLo && w.BoundingBox.Left < money.XHi
                        && Math.Abs(w.BoundingBox.Top - top) <= MoneyTolerance)
            .Select(w => (w.BoundingBox.Top, Value: ParseMoney(w.Text)))
            .Where(x => x.Value is not null)
            .OrderBy(x => x.Top)
            .ToList();

        if (decimals.Count == 0) return (null, null);
        if (decimals.Count == 1) return (decimals[0].Value, null);

        // Ordered by ascending top: the lower-on-page value is retail.
        return (decimals[^1].Value, decimals[0].Value);
    }

    private static decimal? FindDecimalNear(Page page, double top, double xLo, double xHi, double yTol) =>
        page.GetWords()
            .Where(w => w.BoundingBox.Left >= xLo && w.BoundingBox.Left < xHi
                        && Math.Abs(w.BoundingBox.Top - top) <= yTol)
            .Select(w => ParseMoney(w.Text))
            .FirstOrDefault(v => v is not null);

    /// <summary>
    /// The line's ITEMTOTAL. Read letter-wise because the thousands separator is
    /// drawn as a separate glyph well away from the digits.
    /// </summary>
    private static decimal? ReadRowItemTotal(Page page, double top, Bands b)
    {
        // Prefer the word-level value: PdfPig keeps "58,069.28" intact, whereas the
        // letter reader can lose a decimal point whose glyph is spaced away from
        // its digits — and a total silently multiplied by 100 would still reconcile
        // against nothing, so it must not be guessed at.
        var word = page.GetWords()
            .Where(w => w.BoundingBox.Left >= b.ItemTotal.XLo
                        && Math.Abs(w.BoundingBox.Top - top) <= RowTolerance)
            .Select(w => ParseMoney(TrailingMoney(w.Text)))
            .FirstOrDefault(v => v is not null);
        return word;
    }

    /// <summary>The PO-level merchandise total, printed as a merged "Total1154"-style cell.</summary>
    private static decimal? FindMerchandiseTotal(Page page)
    {
        foreach (var pg in new[] { page })
        {
            var label = pg.GetWords().FirstOrDefault(w =>
                w.Text.StartsWith("Merchandise", StringComparison.OrdinalIgnoreCase));
            if (label is null) continue;

            var value = pg.GetWords()
                .Where(w => Math.Abs(w.BoundingBox.Top - label.BoundingBox.Top) <= RowTolerance
                            && w.BoundingBox.Left > label.BoundingBox.Right)
                .Select(w => ParseMoney(TrailingMoney(w.Text)))
                .FirstOrDefault(v => v is not null);
            if (value is not null) return value;
        }
        return null;
    }

    /// <summary>
    /// Colour is printed in the description band on the style row, and may carry a
    /// trailing "-1-1-" pack code that is not part of the colour name.
    /// </summary>
    private static string ReadColor(Page page, double top, Bands b)
    {
        // "Buyers Color Description: BLUE" is printed ABOVE the style row and wraps,
        // leaving its qualifier ("TXT") on the style row itself. Take the labelled
        // value first and append what wrapped, so the colour reads as printed.
        var onRow = CellText(page, top, b.Description.XLo, b.Description.XHi, RowTolerance);
        var dash = onRow.IndexOf('-');
        if (dash > 0) onRow = onRow[..dash];
        onRow = onRow.Trim();

        for (var up = 4.0; up <= 12.0; up += 2.0)
        {
            var above = CellText(page, top + up, b.Description.XLo, b.Description.XHi, 2.5);
            var colonAbove = above.IndexOf(':');
            if (colonAbove < 0) continue;

            var head = above[(colonAbove + 1)..].Trim();
            if (head.Length == 0 || SizeParts().IsMatch(head.Replace(" ", ""))) continue;
            // A bare number here is the colour CODE from the preceding row's block,
            // not this row's colour name — searching upward must not cross rows.
            if (head.All(char.IsDigit)) continue;

            return onRow.Length > 0 && !onRow.Contains(':') ? $"{head} {onRow}" : head;
        }

        if (onRow.Length > 0 && !onRow.Contains(':')) return onRow;

        for (var offset = 8.0; offset <= 46.0; offset += 2.0)
        {
            var text = CellText(page, top - offset, b.Description.XLo, b.Description.XHi, 2.5);
            var idx = text.IndexOf(':');
            if (idx < 0) continue;

            var value = text[(idx + 1)..].Trim();
            if (value.Length == 0 || SizeParts().IsMatch(value.Replace(" ", ""))) continue;

            // "BLUE TXT" wraps: the label line carries the colour and the next
            // line its qualifier. Join them so every row reads the same way.
            var tail = CellText(page, top - offset - 6.7, b.Description.XLo, b.Description.XHi, 2.5).Trim();
            return tail.Length > 0 && !tail.Contains(':') && !SizeParts().IsMatch(tail.Replace(" ", ""))
                ? $"{value} {tail}"
                : value;
        }
        return "";
    }

    /// <summary>
    /// "Size Description: 30 9 IN" is printed on its own line a few points below
    /// the style row; take the text after the colon.
    /// </summary>
    private static string ReadSize(Page page, double top, Bands b)
    {
        // Glyphs of one printed line vary by ~1pt in top, and the label wraps, so
        // this needs a taller window than a single-baseline cell read. The span is
        // generous because the last row on a page has its detail lines pushed
        // further down than the rows above it.
        for (var offset = 8.0; offset <= 46.0; offset += 2.0)
        {
            // Several labelled lines share this band ("Product Description:",
            // "Buyers Color:"), and the labels' glyphs interleave across two tops,
            // so the label text cannot be matched reliably. The size VALUE has a
            // distinctive shape — waist, inseam, unit — so identify it by that.
            var probe = CellText(page, top - offset, b.Description.XLo, b.StoreQty.XLo, 2.5)
                .Replace(" ", "");

            var idx = probe.IndexOf(':');
            if (idx < 0) continue;

            var size = probe[(idx + 1)..].Trim();
            if (size.Length == 0 || !SizeParts().IsMatch(size)) continue;

            // "30 9 IN" arrives as "309IN": re-space it the way it is printed.
            var m = SizeParts().Match(size);
            return $"{m.Groups[1].Value} {m.Groups[2].Value} {m.Groups[3].Value}";
        }
        return "";
    }

    private static bool IsUpc(string? s) =>
        s is not null && s.Length is >= 12 and <= 14 && s.All(char.IsDigit);

    private static decimal? ParseMoney(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var cleaned = s.Replace(",", "").Replace("$", "").Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d : null;
    }

    /// <summary>Pulls the money off a merged cell like "Total58069.28".</summary>
    private static string TrailingMoney(string s)
    {
        var m = MoneyTail().Match(s);
        return m.Success ? m.Groups[1].Value : s;
    }

    [GeneratedRegex(@"^(.*[A-Za-z]\d*?)(\d{12})$")]
    private static partial Regex StyleUpc12();

    [GeneratedRegex(@"^(.*?)(\d{12,13})$")]
    private static partial Regex StyleUpc();

    /// <summary>A style still carrying a UPC: a long trailing digit run.</summary>
    [GeneratedRegex(@"\d{10,}$")]
    private static partial Regex MergedStyle();

    [GeneratedRegex(@"^(\d+)([A-Za-z].*)?$")]
    private static partial Regex LeadingDigits();

    [GeneratedRegex(@"([\d,]+\.\d{2})$")]
    private static partial Regex MoneyTail();

    /// <summary>"309IN" → waist 30, inseam 9, unit IN.</summary>
    [GeneratedRegex(@"^(\d{2})(\d)([A-Za-z]+)$")]
    private static partial Regex SizeParts();
}
