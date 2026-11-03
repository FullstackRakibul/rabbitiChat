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
    private readonly IReadOnlyList<IChatModelSource> _static;
    private readonly Dictionary<string, IChatModelSource> _byKey;
    private readonly ModelProviders.DbModelSourceProvider? _database;

    /// <summary>
    /// Takes every IChatModelSource the container knows about. New sources join
    /// simply by being registered — this constructor never changes. Providers added
    /// in Settings come from <paramref name="database"/>, read on every call, so a
    /// saved one is listed without a restart. A database key can never shadow a
    /// registered one: the admin API refuses it, and the registered source wins here.
    /// </summary>
    public ModelSourceRegistry(IEnumerable<IChatModelSource> sources, ModelProviders.DbModelSourceProvider? database = null)
    {
        _static = sources.ToList();
        _byKey = _static.ToDictionary(s => s.SourceKey, StringComparer.OrdinalIgnoreCase);
        _database = database;
    }

    public IReadOnlyList<IChatModelSource> All => _database is null || _database.Sources.Count == 0
        ? _static
        : _static.Concat(_database.Sources.Where(s => !_byKey.ContainsKey(s.SourceKey))).ToList();

    /// <summary>Keys of the sources registered at startup — the ones a database provider may not take.</summary>
    public IEnumerable<string> RegisteredKeys => _byKey.Keys;

    public string DefaultSourceKey => "local";

    public IChatModelSource? Resolve(string? sourceKey)
    {
        var key = string.IsNullOrWhiteSpace(sourceKey) ? DefaultSourceKey : sourceKey.Trim();
        return _byKey.TryGetValue(key, out var source) ? source : _database?.Resolve(key);
    }
}
