// ম্যাট্রিক্স — which model Matrix asks, and whether this person may (docs/MATRIX_PROMPT.md §3.9).
//
// One place for every Matrix model call — mapping suggestions, the summary, the chat:
// the configured Matrix model, else the installation's extraction or default model,
// else the person's default under ModelPolicy; then ModelPolicy decides.

using RabbitIC.Api.Models;
using RabbitIC.Api.Rbac;

namespace RabbitIC.Api.Matrix;

public sealed class MatrixModel(IConfiguration config, ModelPolicy modelPolicy)
{
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public string? Configured =>
        Blank(config["Matrix:Model"]) ?? Blank(config["Extraction:DefaultModel"]) ?? Blank(config["Ollama:DefaultModel"]);

    /// <summary>How long Ollama keeps the Matrix model loaded between questions, in seconds (Matrix:KeepAlive; 30 minutes).</summary>
    public int KeepAlive => int.TryParse(config["Matrix:KeepAlive"], out var k) ? k : 1800;

    /// <summary>The context window Matrix asks for.</summary>
    public int NumCtx => int.TryParse(config["Matrix:NumCtx"] ?? config["Extraction:NumCtx"], out var n) && n > 0 ? n : 16384;

    /// <summary>The model to use (<paramref name="requested"/> when given), or why there is none this person may use.</summary>
    public async Task<(string? Model, string? Error)> ResolveAsync(User user, string? requested, CancellationToken ct)
    {
        var model = Blank(requested) ?? Configured ?? await modelPolicy.DefaultForAsync(user, ct);
        if (model is null) return (null, "No model is configured for Matrix.");
        var allowed = await modelPolicy.CheckAsync(user, model, ModelPolicy.LocalSource, ct: ct);
        return allowed.Allowed ? (model, null) : (null, allowed.Error);
    }
}
