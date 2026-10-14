using System.Text;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.DTOs;
using RabbitIC.Api.Models;
using RabbitIC.Api.ModelSources;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.MinimalApis
{
    /// <summary>
    /// POST /api/chat — the one turn-taking loop.
    ///
    /// Responsibilities, in order: resolve the conversation, assemble the system
    /// prompt (delegated to <see cref="IContextSourceService"/>, never built here),
    /// stream the model's reply, service any tool call it makes, and persist the
    /// result. Everything it decides is orchestration; the rules themselves live
    /// in the services it calls.
    /// </summary>
    public static class ChatEndpoint
    {
        /// <summary>
        /// How many times the model may call a tool and be handed the results
        /// before we stop and let it answer with what it has. One round covers the
        /// intended "search, then answer"; the cap exists so a model that keeps
        /// asking cannot loop the request forever.
        /// </summary>
        private const int MaxToolRounds = 2;

        /// <summary>
        /// The HR chat's cap (docs/HR_MODULE_PROMPT.md §3.3): an amount can need a
        /// few `calculate` calls in a row — per-day basic, then the benefit, then a
        /// total — so it gets more rounds than one web search needs.
        /// </summary>
        internal const int HrMaxToolRounds = 4;

        public static async Task HandleAsync(
            ChatRequest req,
            HttpContext ctx,
            AppDbContext db,
            ChatService chat,
            IContextSourceService contextSources,
            WebSearchRunner webSearchRunner,
            IConfiguration config,
            ILoggerFactory logs,
            Rbac.ModelPolicy modelPolicy)
        {
            var log = logs.CreateLogger("ChatEndpoint");
            var ct = ctx.RequestAborted;

            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("Unauthorized.");
                return;
            }

            if (string.IsNullOrWhiteSpace(req.Message) || string.IsNullOrWhiteSpace(req.Model))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("model and message are required.");
                return;
            }

            // The model policy, before the conversation and the user's message are
            // stored: a refused request must leave no trace (§3.1, §9).
            var policy = await modelPolicy.CheckAsync(user, req.Model, req.Source, ct: ct);
            if (!policy.Allowed)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsync(policy.Error!);
                return;
            }

            // ---- Conversation ----
            // Scoped to this user, so you cannot post into someone else's thread.
            Conversation convo;
            // Never an HR conversation (Kind = "hr"): those belong to the HR tab.
            if (req.ConversationId is int cid &&
                await db.Conversations.FirstOrDefaultAsync(c => c.Id == cid && c.UserId == user.Id && c.Kind == null, ct) is { } existing)
            {
                convo = existing;
            }
            else
            {
                convo = new Conversation { UserId = user.Id, Title = Authorization.Truncate(req.Message, 40) };
                db.Conversations.Add(convo);
                await db.SaveChangesAsync(ct);
            }

            // The client sends the set it has switched on; when it sends nothing we
            // reuse whatever this conversation last used, so reopening a chat and
            // replying keeps its context instead of silently dropping it.
            var activeIds = req.ActiveSourceIds ?? ParseIds(convo.ActiveSourceIds);
            convo.ActiveSourceIds = string.Join(',', activeIds);

            db.Messages.Add(new Message { ConversationId = convo.Id, Role = "user", Content = req.Message });
            await db.SaveChangesAsync(ct);

            // ---- System prompt ----
            // Base persona from config, then each active source. Assembled by the
            // context service — the same call the workspace tab makes with
            // kind="buyer", which is what keeps the two surfaces in step.
            var basePersona = config["Ollama:SystemPrompt"];
            var systemPrompt = await contextSources.BuildSystemPromptAsync(
                user.Id, activeIds, basePersona, ContextSource.KindSource, ct);

            var history = await db.Messages
                .Where(m => m.ConversationId == convo.Id)
                .OrderBy(m => m.Id)
                .Select(m => new ChatTurn(m.Role, m.Content, null, null))
                .ToListAsync(ct);

            var turns = new List<ChatTurn>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                turns.Add(new ChatTurn("system", systemPrompt));
            turns.AddRange(history);

            // ---- Web search ----
            // Two paths, chosen by what the model can do. A tool-capable model gets
            // the tool and decides for itself; anything else gets one search
            // prepended up front. Never both, and never a control that does nothing.
            var capabilities = await ModelCapabilitiesAsync(chat, req.Source, req.Model, ct);
            var modelSupportsTools = capabilities.Contains("tools", StringComparer.OrdinalIgnoreCase);
            var useTools = req.WebSearch && modelSupportsTools && webSearchRunner.IsConfigured;

            // Three-valued, and the middle case is the one that used to be wrong:
            // a model that CAN reason is told explicitly either way, because
            // saying nothing lets it fall back to its own default (thinking ON
            // for Qwen) and the toggle appeared to do nothing. A model that
            // cannot reason is told nothing at all -- the field is a 400 there.
            var canThink = capabilities.Any(c =>
                c.Equals("thinking", StringComparison.OrdinalIgnoreCase) ||
                c.Equals("reasoning", StringComparison.OrdinalIgnoreCase));

            bool? think = canThink ? req.Think : null;

            var webSearchUsed = false;
            string? notice = null;

            if (req.WebSearch && !useTools)
            {
                if (!webSearchRunner.IsConfigured)
                {
                    notice = "_Web search is not configured on this server — answering without it._\n\n";
                }
                else
                {
                    var context = await webSearchRunner.RunAsync(req.Message, ct);
                    if (context is not null)
                    {
                        turns.Insert(0, new ChatTurn("system", context));
                        webSearchUsed = true;
                    }
                    else
                    {
                        notice = "_Web search returned no usable results — answering without it._\n\n";
                    }
                }
            }

            var tools = useTools ? new[] { WebSearchTool.Definition } : null;

            // ---- Stream ----
            ctx.Response.Headers["X-Conversation-Id"] = convo.Id.ToString();
            ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";

            var writer = new ChatStreamWriter(ctx.Response, ct);
            var answer = new StringBuilder();

            if (notice is not null)
            {
                answer.Append(notice);
                await writer.ContentAsync(notice);
            }

            try
            {
                await RunToolLoopAsync(chat, req.Source, new ChatArgs(req.Model, turns, tools, think), turns,
                    MaxToolRounds, writer, answer, async call =>
                    {
                        var (result, used) = await RunToolAsync(call, webSearchRunner, writer, log, ct);
                        webSearchUsed |= used;
                        return result;
                    }, ct);
            }
            catch (ChatSourceException ex)
            {
                // A failure before any header would be a 400; here the response is
                // already open, so it is reported in-band as its own frame.
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync(ex.Message);
                    return;
                }

                answer.Append($"\n\n_{ex.Message}_");
                await writer.ErrorAsync(ex.Message);
            }
            catch (OperationCanceledException)
            {
                // The user hit Stop. Persist what streamed rather than losing it.
                await PersistAsync(db, convo.Id, answer.ToString(), webSearchUsed, CancellationToken.None);
                return;
            }

            await writer.DoneAsync(webSearchUsed);
            await PersistAsync(db, convo.Id, answer.ToString(), webSearchUsed, ct);
        }

        /// <summary>
        /// The one tool loop (docs/HR_MODULE_PROMPT.md §0.6: there is no second).
        /// Streams the model's answer into <paramref name="answer"/> and the client;
        /// when the model asks for tools, runs each through <paramref name="runTool"/>,
        /// hands the results back and asks again, up to <paramref name="maxRounds"/>.
        /// /api/chat passes web_search; the HR chat passes calculate.
        /// </summary>
        internal static async Task RunToolLoopAsync(
            ChatService chat, string? source, ChatArgs args, List<ChatTurn> turns, int maxRounds,
            ChatStreamWriter writer, StringBuilder answer, Func<ToolCallRequest, Task<string>> runTool,
            CancellationToken ct)
        {
            for (var round = 0; ; round++)
            {
                var pendingCalls = new List<ToolCallRequest>();
                var roundText = new StringBuilder();

                await foreach (var e in chat.StreamAsync(source, args with { Messages = turns }, ct))
                {
                    switch (e.Kind)
                    {
                        case ChatEventKind.Content:
                            answer.Append(e.Text);
                            roundText.Append(e.Text);
                            await writer.ContentAsync(e.Text);
                            break;

                        case ChatEventKind.Thinking:
                            // Reasoning is shown live but never persisted: it is
                            // scratch work, not the answer, and re-reading it on
                            // every future turn would waste the context window.
                            await writer.ThinkingAsync(e.Text);
                            break;

                        case ChatEventKind.ToolCall when e.ToolCall is not null:
                            pendingCalls.Add(e.ToolCall);
                            break;
                    }
                }

                // No tool asked for, or the budget is spent — this was the answer.
                if (pendingCalls.Count == 0 || round >= maxRounds) break;

                // The assistant turn that requested the tools has to be in the
                // history, with its calls and only this round's text: without the
                // calls the model sees results answering nothing and writes its
                // draft again; the answer so far would repeat earlier rounds.
                turns.Add(new ChatTurn("assistant", roundText.ToString(), ToolCalls: pendingCalls));

                foreach (var call in pendingCalls)
                    turns.Add(new ChatTurn("tool", await runTool(call), call.Name));
            }
        }

        /// <summary>
        /// Runs one tool call and reports its progress to the client. Returns the
        /// payload handed back to the model, and whether it counts as web grounding.
        /// </summary>
        private static async Task<(string Result, bool WebSearchUsed)> RunToolAsync(
            ToolCallRequest call, WebSearchRunner runner, ChatStreamWriter writer,
            ILogger log, CancellationToken ct)
        {
            if (!string.Equals(call.Name, WebSearchTool.Name, StringComparison.OrdinalIgnoreCase))
            {
                // Only tools we offered can be called; anything else is the model
                // inventing one, and it is told so rather than left waiting.
                log.LogWarning("Model requested unknown tool {Tool}.", call.Name);
                await writer.ToolAsync(call.Name, "failed", "Unknown tool.");
                return ($"No tool named '{call.Name}' is available.", false);
            }

            var query = WebSearchTool.ReadQuery(call.ArgumentsJson);
            if (string.IsNullOrWhiteSpace(query))
            {
                await writer.ToolAsync(call.Name, "failed", "No query supplied.");
                return ("The web_search call was missing a query.", false);
            }

            await writer.ToolAsync(call.Name, "running", query);

            var results = await runner.RunAsync(query, ct);
            if (results is null)
            {
                await writer.ToolAsync(call.Name, "failed", query);
                return ($"The web search for \"{query}\" returned no usable results.", false);
            }

            await writer.ToolAsync(call.Name, "done", query);

            // Restate the citation rule with the results themselves. Models reliably
            // follow an instruction attached to the tool output they are reading;
            // one buried in the system prompt several turns back gets dropped, and
            // the answer comes back with bare reference markers instead of URLs.
            return (results
                + "\n\nCite the source URL inline after each claim you take from these "
                + "results, as a markdown link. Do not use footnote or reference markers.",
                true);
        }

        /// <summary>
        /// What the chosen model declares it can do. An unknown model reports
        /// nothing, so every capability-gated feature stays off rather than being
        /// sent upstream and rejected.
        /// </summary>
        internal static async Task<IReadOnlyList<string>> ModelCapabilitiesAsync(
            ChatService chat, string? source, string model, CancellationToken ct)
        {
            var models = await chat.ListAllModelsAsync(ct);
            var match = models.FirstOrDefault(m =>
                m.Name.Equals(model, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(source) || m.Source.Equals(source, StringComparison.OrdinalIgnoreCase)));

            return match?.Capabilities ?? Array.Empty<string>();
        }

        private static async Task PersistAsync(
            AppDbContext db, int conversationId, string content, bool webSearchUsed, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(content)) return;   // nothing streamed; do not save an empty turn

            db.Messages.Add(new Message
            {
                ConversationId = conversationId,
                Role = "assistant",
                Content = content,
                WebSearchUsed = webSearchUsed,
            });
            await db.SaveChangesAsync(ct);
        }

        /// <summary>Reads the stored comma list. Malformed entries are dropped, not fatal.</summary>
        private static List<int> ParseIds(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return new List<int>();

            return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var id) ? id : (int?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToList();
        }
    }
}
