using System.Text.Json;

namespace RabbitIC.Api.MinimalApis
{
    /// <summary>
    /// The chat response wire format: one JSON object per line (NDJSON).
    ///
    /// The stream used to be raw text, which was enough when the only thing coming
    /// back was the answer. Reasoning deltas and tool status have to be
    /// distinguishable from the answer itself, and a client cannot recover that
    /// from concatenated text — so each delta is tagged. Line-delimited rather than
    /// SSE because the existing fetch-and-read-chunks client needs no new transport.
    /// </summary>
    public sealed class ChatStreamWriter
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly HttpResponse _response;
        private readonly CancellationToken _ct;

        public ChatStreamWriter(HttpResponse response, CancellationToken ct)
        {
            _response = response;
            _ct = ct;
        }

        /// <summary>A piece of the visible answer.</summary>
        public Task ContentAsync(string text) => WriteAsync(new { type = "content", text });

        /// <summary>A piece of the model's reasoning.</summary>
        public Task ThinkingAsync(string text) => WriteAsync(new { type = "thinking", text });

        /// <summary>
        /// Tool progress, so the UI can show what's happening instead of a stalled
        /// cursor. <paramref name="status"/> is "running" | "done" | "failed".
        /// </summary>
        public Task ToolAsync(string tool, string status, string? detail = null) =>
            WriteAsync(new { type = "tool", tool, status, detail });

        /// <summary>An in-band failure after headers are already sent.</summary>
        public Task ErrorAsync(string message) => WriteAsync(new { type = "error", message });

        /// <summary>Terminal frame carrying what the client needs once the answer is complete.</summary>
        public Task DoneAsync(bool webSearchUsed) =>
            WriteAsync(new { type = "done", webSearchUsed });

        private async Task WriteAsync(object frame)
        {
            await _response.WriteAsync(JsonSerializer.Serialize(frame, Json) + "\n", _ct);
            await _response.Body.FlushAsync(_ct);   // push each frame immediately
        }
    }
}
