using System.Text.Json;

namespace RabbitIC.Api.MinimalApis;

/// <summary>
/// The splitter's NDJSON wire format — one JSON object per line, same shape and
/// transport as <see cref="ChatStreamWriter"/> so the client reuses its existing
/// chunk reader rather than growing a second one.
///
/// Frames are tagged because the UI shows pipeline STAGES, not just text: a
/// stage indicator has to advance independently of the model's token stream, and
/// a client cannot recover that from concatenated content.
/// </summary>
public sealed class SplitterStreamWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpResponse _response;
    private readonly CancellationToken _ct;

    public SplitterStreamWriter(HttpResponse response, CancellationToken ct)
    {
        _response = response;
        _ct = ct;
    }

    /// <summary>
    /// Pipeline progress. <paramref name="stage"/> is one of
    /// parse | facts | reason | merge; <paramref name="status"/> is
    /// running | done | failed.
    /// </summary>
    public Task StageAsync(string stage, string status, string? detail = null) =>
        WriteAsync(new { type = "stage", stage, status, detail });

    /// <summary>The parsed component summary, as soon as Stage 1 finishes.</summary>
    public Task ComponentAsync(object component) =>
        WriteAsync(new { type = "component", component });

    /// <summary>A token from the reasoning stage, so the AI half streams visibly.</summary>
    public Task AiDeltaAsync(string text) =>
        WriteAsync(new { type = "ai", text });

    /// <summary>The terminal frame: the merged proposal for human review.</summary>
    public Task ProposalAsync(object proposal) =>
        WriteAsync(new { type = "proposal", proposal });

    /// <summary>An in-band failure once headers are already sent.</summary>
    public Task ErrorAsync(string message) =>
        WriteAsync(new { type = "error", message });

    private async Task WriteAsync(object frame)
    {
        await _response.WriteAsync(JsonSerializer.Serialize(frame, Json) + "\n", _ct);
        await _response.Body.FlushAsync(_ct);   // push each frame immediately
    }
}
