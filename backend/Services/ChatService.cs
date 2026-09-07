using RabbitIC.Api.ModelSources;

namespace RabbitIC.Api.Services;

/// <summary>
/// Chat orchestration across every model source.
///
/// Depends on <see cref="IModelSourceRegistry"/> only — no concrete source type
/// is named anywhere in this file. That is what
/// makes a third provider (Azure, a second cluster, a mock) a registration-only
/// change: this class, and the endpoints that use it, stay untouched.
/// </summary>
public class ChatService
{
    private readonly IModelSourceRegistry _registry;
    private readonly ILogger<ChatService> _log;

    public ChatService(IModelSourceRegistry registry, ILogger<ChatService> log)
    {
        _registry = registry;
        _log = log;
    }

    /// <summary>Every source's models, merged and tagged. One dead source doesn't fail the call.</summary>
    public async Task<IReadOnlyList<ModelInfo>> ListAllModelsAsync(CancellationToken ct = default)
    {
        var tasks = _registry.All.Select(s => ListSafelyAsync(s, ct));
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).ToList();
    }

    private async Task<IReadOnlyList<ModelInfo>> ListSafelyAsync(IChatModelSource source, CancellationToken ct)
    {
        try
        {
            return await source.ListModelsAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Model source {Source} failed to list: {Message}", source.SourceKey, ex.Message);
            return Array.Empty<ModelInfo>();
        }
    }

    /// <summary>
    /// Streams a reply from the requested source. Throws
    /// <see cref="ChatSourceException"/> for an unknown source, a rejected model,
    /// or an upstream failure — the endpoint maps that to a status code.
    /// </summary>
    public IAsyncEnumerable<string> StreamAsync(
        string? sourceKey, ChatArgs args, CancellationToken ct = default)
    {
        var source = _registry.Resolve(sourceKey)
            ?? throw new ChatSourceException($"Unknown model source '{sourceKey}'.");

        // Embedding models can't answer chat on any source; reject before the call.
        if (ModelNaming.IsEmbeddingModel(args.Model))
            throw new ChatSourceException($"'{args.Model}' is an embedding model and cannot chat.");

        return source.StreamChatAsync(args, ct);
    }
}
