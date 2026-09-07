namespace RabbitIC.Api.ModelSources;

/// <summary>
/// A third source, present to prove the Open/Closed claim: adding it required
/// this file plus one AddSingleton line in Program.cs, and touched neither
/// ChatService nor the /api/models and /api/chat endpoints.
///
/// It streams the prompt back word by word, which also makes it a dependency-free
/// way to exercise the chat pipeline when no model server is reachable.
/// Enabled only when "ModelSources:EnableEcho" is true, so it stays out of the
/// picker in normal use.
/// </summary>
public class EchoModelSource : IChatModelSource
{
    private const string ModelName = "echo:test";

    public string SourceKey => "echo";

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ModelInfo> models = new[]
        {
            new ModelInfo(ModelName, SourceKey, ContextLength: 0,
                          Capabilities: new[] { "chat", "diagnostic" }, Metered: false),
        };
        return Task.FromResult(models);
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        ChatArgs args,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var last = args.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";

        foreach (var word in last.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
            yield return word + " ";
        }
    }
}
