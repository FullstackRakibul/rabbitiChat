namespace RabbitIC.Api.ModelSources;

/// <summary>
/// Naming rules that hold across every source, so no consumer has to reach into
/// a concrete implementation to apply them.
/// </summary>
public static class ModelNaming
{
    /// <summary>
    /// Installed alongside chat models but rejected by /api/chat ("does not
    /// support chat"), so they never belong in a picker or a chat request.
    /// </summary>
    private static readonly string[] EmbeddingMarkers = { "embed", "bge-", "gte-", "e5-" };

    public static bool IsEmbeddingModel(string name) =>
        EmbeddingMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
