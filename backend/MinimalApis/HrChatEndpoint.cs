// মানব সম্পদ — POST /api/hr/chat (docs/HR_MODULE_PROMPT.md §3.3).
//
// One HR question, answered from HR's library only:
//
//   1. Access: tab `hr` (TabAccessMiddleware) and ModelPolicy on the model —
//      before anything is saved, so a refusal leaves no trace.
//   2. The system message: persona + every active `always` item, whole, in
//      SortOrder. Over 8,000 tokens it is refused, naming the largest items —
//      never cut. (The Library's always cap keeps a library built through the
//      UI from ever getting here.)
//   3. Excerpts (HrLibrary.SelectExcerptsAsync): hybrid search, scoped to the
//      "Search in" items when given, the best 5, whole chunks, up to 1,500 tokens.
//      A status line goes out before retrieval and again when it is done, so the
//      person sees something within a second.
//   4. History fills what is left of the 16,384-token window after 2,000 are
//      reserved for the answer, the oldest turns dropped first.
//   5. The message order is fixed for Ollama's prompt cache: the unchanged system
//      message, then history, then one last user message with the excerpts and
//      the question. num_ctx, keep_alive and temperature are sent the same every time.
//   6. No tools. The model writes amounts as [[= expression ]]; HrFormulaStream
//      puts each result in its place as the answer streams.
//   7. The number check runs on the finished answer, with formula results counted
//      as verified; amounts it cannot trace, and formulas that could not be
//      calculated, are streamed as a final "unverified" frame and kept with the answer.
//
// Frames: "status" (twice), "context" (the receipt the "Context used" panel
// shows), then content frames with a "formula" frame after each result, "error",
// "unverified" (amounts, formulas, and citations that name nothing sent), and "done".

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Extraction;
using RabbitIC.Api.Hr;
using RabbitIC.Api.ModelSources;
using RabbitIC.Api.Models;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.MinimalApis
{
    /// <param name="Items">"Search in": retrieval limited to these library items. Empty or null: the whole library.</param>
    public record HrChatRequest(int? ConversationId, string? Message, string? Model, List<int>? Items);

    public static class HrChatEndpoint
    {
        public const int ReservedForAnswer = 2000;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>The HTTP side only: who is asking, the refusal as a response, the stream's headers. The chat is IHrChatService's.</summary>
        public static async Task HandleAsync(HrChatRequest req, HttpContext ctx, AppDbContext db, IHrChatService hr)
        {
            var ct = ctx.RequestAborted;

            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("Unauthorized."); return; }

            var refused = await hr.AskAsync(user, req, conversationId =>
            {
                ctx.Response.Headers["X-Conversation-Id"] = conversationId.ToString();
                ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
                return new ChatStreamWriter(ctx.Response, ct);
            }, ct);
            if (refused is null) return;
            ctx.Response.StatusCode = refused.Status;
            if (refused.Json is not null) await ctx.Response.WriteAsJsonAsync(refused.Json, ct);
            else await ctx.Response.WriteAsync(refused.Text!);
        }
    }
}
