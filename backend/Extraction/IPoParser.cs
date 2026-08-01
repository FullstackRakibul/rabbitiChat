namespace RabbitIC.Api.Extraction;

/// <summary>
/// Contract for deterministic PO extraction.
///
/// Each buyer template needs its own positional rules, but the pipeline around
/// them — detect, parse, validate, gate — stays identical, so no buyer can end up
/// bypassing the arithmetic checks.
/// </summary>
public interface IPoParser
{
    /// <summary>Stored on BuyerProfile.ParserKey. Must be unique.</summary>
    string Key { get; }

    /// <summary>Human label for the review UI.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Confidence that this parser owns the document, 0..1, judged from page-1
    /// text and PDF metadata. Used when no buyer profile pattern matched.
    /// </summary>
    double Detect(string firstPageText, string? pdfProducer);

    /// <summary>Throws when the layout has drifted; the dispatcher catches and falls back.</summary>
    ExtractionResult Parse(Stream pdfStream);
}
