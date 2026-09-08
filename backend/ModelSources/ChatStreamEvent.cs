namespace RabbitIC.Api.ModelSources;

/// <summary>
/// What a source can emit while answering. Sources used to yield bare strings;
/// reasoning models and tool calls need two more kinds of delta, and collapsing
/// them into the text stream would leave the UI unable to tell them apart.
/// </summary>
public enum ChatEventKind
{
    /// <summary>A piece of the visible answer.</summary>
    Content,

    /// <summary>A piece of the model's reasoning, shown in the collapsible block.</summary>
    Thinking,

    /// <summary>The model asked for a tool. <see cref="ChatStreamEvent.ToolCall"/> is set.</summary>
    ToolCall,
}

/// <summary>One tool invocation requested by the model.</summary>
/// <param name="Name">Tool name as declared in the tools array, e.g. "web_search".</param>
/// <param name="ArgumentsJson">Raw JSON object of arguments, as the model produced it.</param>
public record ToolCallRequest(string Name, string ArgumentsJson);

/// <summary>A single delta from a model source.</summary>
public record ChatStreamEvent(ChatEventKind Kind, string Text = "", ToolCallRequest? ToolCall = null)
{
    public static ChatStreamEvent Content(string text) => new(ChatEventKind.Content, text);
    public static ChatStreamEvent Thinking(string text) => new(ChatEventKind.Thinking, text);
    public static ChatStreamEvent Tool(ToolCallRequest call) => new(ChatEventKind.ToolCall, ToolCall: call);
}

/// <summary>
/// A tool offered to the model, in Ollama's OpenAI-shaped `tools` array form.
/// </summary>
/// <param name="Name">Function name the model calls.</param>
/// <param name="Description">What it does — the model routes on this.</param>
/// <param name="ParametersJson">JSON Schema object for the arguments.</param>
public record ToolDefinition(string Name, string Description, string ParametersJson);
