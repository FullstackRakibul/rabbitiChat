// Jiji — one model call, through ChatService and ModelSourceRegistry like every other
// feature, under the caller's ModelPolicy. The reply is read as one JSON object;
// prose, code fences and a <think> block around it are ignored, anything else is
// malformed and the caller decides whether to ask once more.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RabbitIC.Api.ModelSources;
using RabbitIC.Api.Models;
using RabbitIC.Api.Rbac;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.Jiji.Services;

public sealed record JijiModelChoice(string Model, string Source);

public sealed partial class JijiLlm(ChatService chat, ModelPolicy policy, IOptions<JijiOptions> options, ILogger<JijiLlm> log)
{
    /// <summary>A long local generation is still bounded; the request's own token cancels sooner.</summary>
    private static readonly TimeSpan CallLimit = TimeSpan.FromMinutes(5);

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    /// <summary>The request's choice, else Jiji:Model on Jiji:ModelSource; null with the reason when neither is usable.</summary>
    public async Task<(JijiModelChoice? Choice, JijiResultKind Refusal, string? Error)> ChooseAsync(
        User user, string? model, string? source, CancellationToken ct)
    {
        var opt = options.Value;
        var name = !string.IsNullOrWhiteSpace(model) ? model.Trim() : opt.Model?.Trim();
        var src = !string.IsNullOrWhiteSpace(model) ? (source?.Trim() ?? "local") : (opt.ModelSource?.Trim() ?? "local");
        if (string.IsNullOrWhiteSpace(name))
            return (null, JijiResultKind.Invalid, "Pick a model, or set Jiji:Model for a default.");

        var check = await policy.CheckAsync(user, name, src, ct: ct);
        if (!check.Allowed) return (null, JijiResultKind.Forbidden, check.Error);
        return (new JijiModelChoice(name!, src), JijiResultKind.Ok, null);
    }

    /// <summary>The visible reply as text. Throws <see cref="ChatSourceException"/> on an upstream failure.</summary>
    public async Task<string> AskAsync(JijiModelChoice m, IReadOnlyList<ChatTurn> turns, double temperature, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(CallLimit);
        var text = new StringBuilder();
        var started = DateTime.UtcNow;
        // Filling a JSON shape needs no reasoning pass, and on a local thinking model that pass
        // is most of the time (minutes). `think` goes only to models that declare it (OllamaChatStream).
        var think = await DeclaresThinkingAsync(m, ct) ? false : (bool?)null;
        try
        {
            await foreach (var e in chat.StreamAsync(m.Source, new ChatArgs(m.Model, turns, Think: think, Temperature: temperature), limit.Token))
                if (e.Kind == ChatEventKind.Content) text.Append(e.Text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ChatSourceException($"'{m.Model}' did not finish within {CallLimit.TotalMinutes:0} minutes.");
        }
        log.LogInformation("Jiji: {Model} ({Source}) answered {Chars} chars in {Seconds:0.0} s.",
            m.Model, m.Source, text.Length, (DateTime.UtcNow - started).TotalSeconds);
        return text.ToString();
    }

    private async Task<bool> DeclaresThinkingAsync(JijiModelChoice m, CancellationToken ct)
    {
        var models = await chat.ListAllModelsAsync(ct);
        return models.Any(x => x.Source.Equals(m.Source, StringComparison.OrdinalIgnoreCase)
            && ModelPolicy.SameModel(x.Name, m.Model)
            && x.Capabilities.Any(c => c.Equals("thinking", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The one JSON object in a reply, or null.</summary>
    public static JsonElement? ExtractObject(string reply)
    {
        var s = ThinkBlock().Replace(reply ?? "", "");
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(s[start..(end + 1)]);
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }
}
