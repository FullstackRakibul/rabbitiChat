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
// "unverified", and "done".

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

        public static async Task HandleAsync(
            HrChatRequest req, HttpContext ctx, AppDbContext db, ChatService chat, HrLibrary library,
            Rbac.ModelPolicy modelPolicy, IConfiguration config, ILoggerFactory logs)
        {
            var log = logs.CreateLogger("HrChat");
            var ct = ctx.RequestAborted;

            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("Unauthorized."); return; }

            var question = (req.Message ?? "").Trim();
            if (question.Length == 0) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("message is required."); return; }

            // ---- 1. the model, through the one policy ----
            var model = string.IsNullOrWhiteSpace(req.Model)
                ? await modelPolicy.DefaultForAsync(user, ct) ?? config["Hr:Model"] ?? "gemma4:12b"
                : req.Model.Trim();
            var policy = await modelPolicy.CheckAsync(user, model, Rbac.ModelPolicy.LocalSource, ct: ct);
            if (!policy.Allowed)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsync(policy.Error!);
                return;
            }

            var window = int.TryParse(config["Hr:NumCtx"], out var w) && w > 0 ? w : 16384;
            var keepAlive = int.TryParse(config["Hr:KeepAlive"], out var k) ? k : -1;
            // The same question gets the same answer, and the model keeps to the rules
            // (a formula for every amount) rather than sampling around them.
            var temperature = double.TryParse(config["Hr:Temperature"], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var tv) ? tv : 0;

            // ---- 2. persona + always items, never cut ----
            var always = await library.AlwaysItemsAsync(ct);
            var system = HrPrompt.System(always);
            var systemTokens = PoPromptBuilder.EstimateTokens(system);
            if (systemTokens > HrLibrary.AlwaysCapTokens)
            {
                var largest = always.OrderByDescending(i => PoPromptBuilder.EstimateTokens(i.Content)).Take(3)
                    .Select(i => $"{i.Name} ({PoPromptBuilder.EstimateTokens(i.Content):N0} tokens)");
                ctx.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = $"The persona and always items use {systemTokens:N0} tokens, more than 8,000. Largest: "
                          + $"{string.Join(", ", largest)}. Make one of them Search, or switch it off, in the Library.",
                }, ct);
                return;
            }

            // ---- the conversation (an HR one, this user's) ----
            Conversation convo;
            if (req.ConversationId is int cid &&
                await db.Conversations.FirstOrDefaultAsync(c => c.Id == cid && c.UserId == user.Id && c.Kind == ContextSource.KindHr, ct) is { } existing)
            {
                convo = existing;
            }
            else
            {
                convo = new Conversation { UserId = user.Id, Kind = ContextSource.KindHr, Title = Authorization.Truncate(question, 40) };
                db.Conversations.Add(convo);
                await db.SaveChangesAsync(ct);
            }

            var earlier = await db.Messages.AsNoTracking()
                .Where(m => m.ConversationId == convo.Id).OrderBy(m => m.Id)
                .Select(m => new { m.Role, m.Content }).ToListAsync(ct);

            db.Messages.Add(new Message { ConversationId = convo.Id, Role = "user", Content = question });
            await db.SaveChangesAsync(ct);

            // The stream starts here, before retrieval, so the status line is on screen at once.
            ctx.Response.Headers["X-Conversation-Id"] = convo.Id.ToString();
            ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
            var writer = new ChatStreamWriter(ctx.Response, ct);
            await writer.FrameAsync(new { type = "status", text = HrPrompt.Searching, found = (int?)null });

            // ---- 3. excerpts: the best 5, whole chunks, up to 1,500 tokens ----
            var excerpts = await library.SelectExcerptsAsync(question, req.Items, ct);
            await writer.FrameAsync(new { type = "status", text = HrPrompt.Found(excerpts.Count), found = (int?)excerpts.Count });
            var excerptBlock = HrPrompt.Excerpts(excerpts);
            var last = HrPrompt.Question(excerpts, question);
            var questionTokens = PoPromptBuilder.EstimateTokens(question);
            var excerptsTokens = PoPromptBuilder.EstimateTokens(last) - questionTokens;

            // ---- 4. history: what fits, newest kept ----
            var room = window - ReservedForAnswer - systemTokens - excerptsTokens - questionTokens;
            var history = new List<ChatTurn>();
            var historyTokens = 0;
            for (var i = earlier.Count - 1; i >= 0; i--)
            {
                var t = PoPromptBuilder.EstimateTokens(earlier[i].Content);
                if (historyTokens + t > room) break;
                history.Insert(0, new ChatTurn(earlier[i].Role, earlier[i].Content));
                historyTokens += t;
            }
            var dropped = earlier.Count - history.Count;

            // ---- 5. the fixed order: system, history, excerpts + question ----
            var turns = new List<ChatTurn> { new("system", system) };
            turns.AddRange(history);
            turns.Add(new ChatTurn("user", last));

            var receipt = new
            {
                type = "context",
                model,
                always = always.Select(i => new { itemId = i.Id, name = i.Name, version = i.Version, tokens = PoPromptBuilder.EstimateTokens(i.Content) }),
                excerpts = excerpts.Select(e => new
                {
                    chunkId = e.ChunkId, itemId = e.ItemId, name = e.ItemName, version = e.Version, sourceRef = e.SourceRef,
                    lineFrom = e.LineFrom, lineTo = e.LineTo, tokens = e.Tokens, score = e.Score,
                }),
                scope = req.Items ?? new List<int>(),
                historyTurns = history.Count,
                historyDropped = dropped,
                tokens = new
                {
                    system = systemTokens, excerpts = excerptsTokens, history = historyTokens, question = questionTokens,
                    window, reserved = ReservedForAnswer,
                },
            };

            await writer.FrameAsync(receipt);

            // ---- 6. the model, no tools; formulas evaluated as the answer streams ----
            var capabilities = await ChatEndpoint.ModelCapabilitiesAsync(chat, Rbac.ModelPolicy.LocalSource, model, ct);
            bool? think = capabilities.Any(c => c.Equals("thinking", StringComparison.OrdinalIgnoreCase)
                                             || c.Equals("reasoning", StringComparison.OrdinalIgnoreCase)) ? false : null;
            var args = new ChatArgs(model, turns, Tools: null, think, window, keepAlive, temperature);

            var answer = new StringBuilder();
            var formulas = new HrFormulaStream();

            async Task SendAsync(IEnumerable<HrFormulaPiece> pieces)
            {
                foreach (var piece in pieces)
                {
                    answer.Append(piece.Text);
                    await writer.ContentAsync(piece.Text);
                    if (piece.Formula is { } f)
                        await writer.FrameAsync(new
                        {
                            type = "formula", offset = f.Offset, length = f.Display.Length,
                            expression = f.Expression, display = f.Display, ok = f.Ok, error = f.Error,
                        });
                }
            }

            try
            {
                await foreach (var e in chat.StreamAsync(Rbac.ModelPolicy.LocalSource, args, ct))
                    if (e.Kind == ChatEventKind.Content) await SendAsync(formulas.Push(e.Text));
                await SendAsync(formulas.Finish());
            }
            catch (ChatSourceException ex)
            {
                await SendAsync(formulas.Finish());
                answer.Append($"\n\n_{ex.Message}_");
                await writer.ErrorAsync(ex.Message);
            }
            catch (OperationCanceledException)
            {
                await SaveAsync(db, convo.Id, answer.ToString(), receipt, formulas.Formulas, Array.Empty<decimal>(), Array.Empty<string>(), CancellationToken.None);
                return;
            }

            // ---- 7. the number check: formula results count as verified ----
            var userText = earlier.Where(m => m.Role == "user").Select(m => m.Content).Append(question);
            var calculated = formulas.Formulas.Where(f => f.Ok).Select(f => f.Value!.Value);
            var unverified = HrNumberCheck.Unverified(answer.ToString(), userText.Concat(new[] { system, excerptBlock }), calculated);
            var failed = formulas.Formulas.Where(f => !f.Ok).Select(f => f.Expression).ToList();
            await writer.FrameAsync(new { type = "unverified", amounts = unverified, formulas = failed });
            await writer.DoneAsync(false);

            await SaveAsync(db, convo.Id, answer.ToString(), receipt, formulas.Formulas, unverified, failed, ct);
            log.LogInformation("HR chat: {Excerpts} excerpts, {History} history turns ({Dropped} dropped), {Formulas} formulas ({Failed} failed), {Unverified} unverified.",
                excerpts.Count, history.Count, dropped, formulas.Formulas.Count, failed.Count, unverified.Count);
        }

        /// <summary>The answer, with what the "Context used" panel, the formulas and the warning line need on reopening.</summary>
        private static async Task SaveAsync(AppDbContext db, int conversationId, string answer, object receipt,
            IReadOnlyList<HrFormula> formulas, IReadOnlyList<decimal> unverified, IReadOnlyList<string> failedFormulas, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(answer)) return;
            db.Messages.Add(new Message
            {
                ConversationId = conversationId,
                Role = "assistant",
                Content = answer,
                HrMetaJson = JsonSerializer.Serialize(new
                {
                    context = receipt,
                    formulas = formulas.Select(f => new
                    {
                        offset = f.Offset, length = f.Display.Length, expression = f.Expression,
                        display = f.Display, ok = f.Ok, error = f.Error,
                    }),
                    unverified,
                    unverifiedFormulas = failedFormulas,
                }, Json),
            });
            await db.SaveChangesAsync(ct);
        }
    }
}
