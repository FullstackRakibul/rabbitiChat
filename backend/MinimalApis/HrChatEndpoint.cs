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
//   3. Excerpts: hybrid search, scoped to the "Search in" items when given, top 8,
//      whole chunks only, until 4,000 tokens.
//   4. History fills what is left of the 16,384-token window after 2,000 are
//      reserved for the answer, the oldest turns dropped first.
//   5. The message order is fixed for Ollama's prompt cache: the unchanged system
//      message, then history, then one last user message with the excerpts and
//      the question. num_ctx and keep_alive are sent the same every time.
//   6. The model runs in the one shared tool loop (ChatEndpoint) with one tool,
//      `calculate`, up to 4 rounds.
//   7. The number check runs on the finished answer; amounts it cannot trace are
//      streamed as a final "unverified" frame and kept with the answer.
//
// Frames: "context" (the receipt the "Context used" panel shows) first, then the
// usual content/tool/error frames, then "unverified", then "done".

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
        public const int ExcerptBudget = 4000;
        public const int MaxExcerpts = 8;

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
            // (calculate for every amount) rather than sampling around them.
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

            // ---- 3. excerpts: whole chunks, top 8, until 4,000 tokens ----
            var excerpts = new List<HrExcerpt>();
            var excerptTokens = 0;
            foreach (var hit in await library.SearchAsync(question, req.Items, MaxExcerpts, ct))
            {
                var tokens = PoPromptBuilder.EstimateTokens(hit.Text);
                if (excerptTokens + tokens > ExcerptBudget) continue;   // whole chunks only; a smaller one may still fit
                excerpts.Add(new HrExcerpt(hit.ChunkId, hit.ItemId, hit.ItemName, hit.Version, hit.SourceRef, hit.LineFrom, hit.LineTo, hit.Text, tokens, hit.Score));
                excerptTokens += tokens;
            }
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

            ctx.Response.Headers["X-Conversation-Id"] = convo.Id.ToString();
            ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
            var writer = new ChatStreamWriter(ctx.Response, ct);
            await writer.FrameAsync(receipt);

            // ---- 6. the shared tool loop, with calculate ----
            var capabilities = await ChatEndpoint.ModelCapabilitiesAsync(chat, Rbac.ModelPolicy.LocalSource, model, ct);
            bool? think = capabilities.Any(c => c.Equals("thinking", StringComparison.OrdinalIgnoreCase)
                                             || c.Equals("reasoning", StringComparison.OrdinalIgnoreCase)) ? false : null;
            var args = new ChatArgs(model, turns, new[] { HrCalculator.Definition }, think, window, keepAlive, temperature);

            var answer = new StringBuilder();
            var calculations = new List<(string Expression, string Result)>();
            var calculated = new List<decimal>();

            async Task<string> RunTool(ToolCallRequest call)
            {
                if (!string.Equals(call.Name, HrCalculator.ToolName, StringComparison.OrdinalIgnoreCase))
                {
                    await writer.ToolAsync(call.Name, "failed", "Unknown tool.");
                    return $"No tool named '{call.Name}' is available.";
                }
                var expression = ReadExpression(call.ArgumentsJson);
                var (value, error) = HrCalculator.Evaluate(expression);
                if (value is null)
                {
                    await writer.ToolAsync(HrCalculator.ToolName, "failed", $"{expression} — {error}");
                    return $"calculate refused \"{expression}\": {error}";
                }
                var result = HrCalculator.Format(value.Value);
                calculations.Add((expression ?? "", result));
                calculated.Add(value.Value);
                await writer.ToolAsync(HrCalculator.ToolName, "done", $"{expression} = {result}");
                return $"{expression} = {result}";
            }

            try
            {
                await ChatEndpoint.RunToolLoopAsync(chat, Rbac.ModelPolicy.LocalSource, args, turns,
                    ChatEndpoint.HrMaxToolRounds, writer, answer, RunTool, ct);
            }
            catch (ChatSourceException ex)
            {
                answer.Append($"\n\n_{ex.Message}_");
                await writer.ErrorAsync(ex.Message);
            }
            catch (OperationCanceledException)
            {
                await SaveAsync(db, convo.Id, answer.ToString(), receipt, calculations, Array.Empty<decimal>(), CancellationToken.None);
                return;
            }

            // ---- 7. the number check ----
            var userText = earlier.Where(m => m.Role == "user").Select(m => m.Content).Append(question);
            var unverified = HrNumberCheck.Unverified(answer.ToString(), userText.Concat(new[] { system, excerptBlock }), calculated);
            await writer.FrameAsync(new { type = "unverified", amounts = unverified });
            await writer.DoneAsync(false);

            await SaveAsync(db, convo.Id, answer.ToString(), receipt, calculations, unverified, ct);
            log.LogInformation("HR chat: {Excerpts} excerpts, {History} history turns ({Dropped} dropped), {Calcs} calculations, {Unverified} unverified.",
                excerpts.Count, history.Count, dropped, calculations.Count, unverified.Count);
        }

        private static string? ReadExpression(string argumentsJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("expression", out var e)
                    ? e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString()
                    : null;
            }
            catch (JsonException) { return null; }
        }

        /// <summary>The answer, with what the "Context used" panel and the warning line need on reopening.</summary>
        private static async Task SaveAsync(AppDbContext db, int conversationId, string answer, object receipt,
            List<(string Expression, string Result)> calculations, IReadOnlyList<decimal> unverified, CancellationToken ct)
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
                    calculations = calculations.Select(c => new { expression = c.Expression, result = c.Result }),
                    unverified,
                }, Json),
            });
            await db.SaveChangesAsync(ct);
        }
    }
}
