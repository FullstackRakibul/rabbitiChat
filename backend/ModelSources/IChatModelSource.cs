namespace RabbitIC.Api.ModelSources;

/// <summary>One model a source can serve, as the picker sees it.</summary>
/// <param name="Name">Display/selection name, as the client sends it back.</param>
/// <param name="Source">Owning source key — "local" or "cloud".</param>
/// <param name="ContextLength">Context window when the source knows it; null otherwise.</param>
/// <param name="Capabilities">Free-form tags, e.g. "chat", "tools", "vision".</param>
/// <param name="Metered">True when calls cost money / consume a quota upstream.</param>
public record ModelInfo(
    string Name,
    string Source,
    int? ContextLength,
    IReadOnlyList<string> Capabilities,
    bool Metered);

/// <summary>A chat turn to stream, independent of which backend answers it.</summary>
/// <param name="Model">Model name as chosen in the picker (pre-suffix mapping).</param>
/// <param name="Messages">Full history, oldest first, system messages included.</param>
/// <param name="Tools">
/// Tools the model may call. Empty means none are offered — the caller decides
/// that from the model's declared capabilities, so a model without tool support
/// is never sent a tools array.
/// </param>
/// <param name="Think">
/// Ask the model to expose its reasoning. The caller sets this only for models
/// that declare the capability, so a source can pass it straight through.
/// </param>
public record ChatArgs(
    string Model,
    IReadOnlyList<ChatTurn> Messages,
    IReadOnlyList<ToolDefinition>? Tools = null,
    bool? Think = null);

/// <summary>
/// One message in a chat history.
/// </summary>
/// <param name="Role">"system" | "user" | "assistant" | "tool".</param>
/// <param name="Content">Message text, or a tool's result payload when Role is "tool".</param>
/// <param name="ToolName">Set on a "tool" turn so the model can match the result to its call.</param>
public record ChatTurn(string Role, string Content, string? ToolName = null);

/// <summary>
/// A place chat models can be listed and streamed from.
///
/// Deliberately narrow (Interface Segregation): listing and streaming are the
/// only things every source must do. Consumers depend on this abstraction, never
/// on a concrete source, so a new provider is an added registration rather than
/// an edit to the chat pipeline (Open/Closed, Dependency Inversion).
/// </summary>
public interface IChatModelSource
{
    /// <summary>Stable key used in DTOs and in the request's `source` field.</summary>
    string SourceKey { get; }

    /// <summary>
    /// Models this source can serve right now. Returns empty (never throws) when
    /// the source is unreachable or unconfigured, so one failing source cannot
    /// blank the merged list.
    /// </summary>
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// Streams the reply as typed deltas — visible content, reasoning, and tool
    /// calls. Throws <see cref="ChatSourceException"/> on upstream failure.
    /// </summary>
    IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatArgs args, CancellationToken ct = default);
}

/// <summary>Upstream failure a source wants surfaced to the caller as a clean status.</summary>
public class ChatSourceException : Exception
{
    public ChatSourceException(string message, Exception? inner = null) : base(message, inner) { }
}
