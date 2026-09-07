namespace RabbitIC.Api.ModelSources;

/// <summary>
/// Resolves a chat source by key and enumerates them all.
///
/// This is the seam that keeps the rest of the app Open/Closed: consumers ask
/// the registry for "cloud" instead of newing a CloudOllamaSource, so adding a
/// third provider is one DI registration and zero edits here or in ChatService.
/// </summary>
public interface IModelSourceRegistry
{
    /// <summary>Every registered source, in registration order.</summary>
    IReadOnlyList<IChatModelSource> All { get; }

    /// <summary>The source with this key, or null when nothing is registered for it.</summary>
    IChatModelSource? Resolve(string? sourceKey);

    /// <summary>Key used when a request doesn't name one — local, i.e. the private default.</summary>
    string DefaultSourceKey { get; }
}

public class ModelSourceRegistry : IModelSourceRegistry
{
    private readonly Dictionary<string, IChatModelSource> _byKey;

    /// <summary>
    /// Takes every IChatModelSource the container knows about. New sources join
    /// simply by being registered — this constructor never changes.
    /// </summary>
    public ModelSourceRegistry(IEnumerable<IChatModelSource> sources)
    {
        All = sources.ToList();
        _byKey = All.ToDictionary(s => s.SourceKey, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IChatModelSource> All { get; }

    public string DefaultSourceKey => "local";

    public IChatModelSource? Resolve(string? sourceKey)
    {
        var key = string.IsNullOrWhiteSpace(sourceKey) ? DefaultSourceKey : sourceKey.Trim();
        return _byKey.TryGetValue(key, out var source) ? source : null;
    }
}
