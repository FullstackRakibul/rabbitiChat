namespace RabbitIC.Api.Extraction;

/// <summary>
/// The shape every extraction path produces, deterministic or model-derived, so
/// the validation gate can be applied identically to both. Serialized camelCase
/// straight to the review UI — property names here are the API contract.
/// </summary>
public sealed class ExtractionResult
{
    public PoHeader Header { get; set; } = new();
    public OrderLine OrderLine { get; set; } = new();
    public List<PrepackComponent> PrepackComponents { get; set; } = new();
    public DerivedTotals Derived { get; set; } = new();
    public decimal? PurchaseOrderTotal { get; set; }
    public List<ValidationCheck> Validation { get; set; } = new();
}

public sealed class PoHeader
{
    public string? Buyer { get; set; }
    public string? OrderNumber { get; set; }
    public string? PoType { get; set; }
    public DateOnly? PoDate { get; set; }
    public DateOnly? CancelDate { get; set; }
    public DateWindow? DeliveryWindow { get; set; }
    public DateWindow? ShippingWindow { get; set; }
    public DateOnly? ExitFactoryEndDate { get; set; }
    public string? Incoterms { get; set; }
    public PartyRef? Vendor { get; set; }
    public PartyRef? ManufacturingPlant { get; set; }
}

public sealed record DateWindow(DateOnly? Start, DateOnly? End);

/// <summary>Vendor / plant as printed: an EDI location id plus name and country.</summary>
public sealed class PartyRef
{
    public string? LocationId { get; set; }
    public string? Name { get; set; }
    public string? Country { get; set; }
}

public sealed class OrderLine
{
    public string? StyleNumber { get; set; }
    public string? Sku { get; set; }
    public string? Upc { get; set; }
    public string? StoreNumber { get; set; }
    public decimal? UnitPrice { get; set; }

    /// <summary>On a prepack PO this counts PACKS, not garments.</summary>
    public int? Quantity { get; set; }

    public decimal? ItemTotal { get; set; }
    public string? Uom { get; set; }
    public bool IsPrepack { get; set; }
}

public sealed class PrepackComponent
{
    public int Line { get; set; }
    public string? Sku { get; set; }
    public string? StyleNumber { get; set; }
    public string? Upc { get; set; }
    public string? ProductDescription { get; set; }
    public string? Color { get; set; }
    public string? ColorCode { get; set; }
    public string? Size { get; set; }
    public string? SizeCode { get; set; }

    /// <summary>A size may legitimately repeat within one pack — never collapse to 1.</summary>
    public int QtyPerPrepack { get; set; }

    public decimal UnitCost { get; set; }
    public decimal RetailPrice { get; set; }
    public string? Uom { get; set; }
}

/// <summary>Computed from the rows, never extracted — the review UI renders these read-only.</summary>
public sealed class DerivedTotals
{
    public int? PiecesPerPrepack { get; set; }
    public int? TotalPrepacks { get; set; }
    public int? TotalPieces { get; set; }
    public decimal? ComputedUnitPrice { get; set; }
    public decimal? ComputedItemTotal { get; set; }
    public List<SizeCount> SizeBreakdown { get; set; } = new();
}

public sealed record SizeCount(string Size, int QtyPerPack, int TotalPieces);

/// <param name="Check">Stable identifier, usable as a test name.</param>
/// <param name="Passed">False puts the whole extraction into needs_review.</param>
/// <param name="Detail">Human-readable: what was compared and what differed.</param>
public sealed record ValidationCheck(string Check, bool Passed, string Detail);
