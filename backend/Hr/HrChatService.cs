// মানব সম্পদ — the HR chat itself (docs/HR_MODULE_PROMPT.md §3.3), moved out of HrChatEndpoint
// unchanged so that a second front door (the HRIS Workspace's Ask, docs/HRIS_WORKSPACE_PROMPT.md
// §3.3) calls the same code: the same prompt in the same order, the same formulas, the same number
// check, the same saved message. POST /api/hr/chat is a thin wrapper over it; hr-eval proves it.
//
// The steps, the frames and their order are described at the top of HrChatEndpoint.cs.

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Extraction;
using RabbitIC.Api.MinimalApis;
using RabbitIC.Api.ModelSources;
using RabbitIC.Api.Models;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.Hr;

/// <summary>A question refused before anything streamed: the status and either a text or a JSON body, as the endpoint always sent them.</summary>
public sealed record HrChatRefusal(int Status, string? Text, object? Json);

public interface IHrChatService
{
    /// <summary>
    /// Answers one HR question for <paramref name="user"/>. Returns a refusal before anything is saved
    /// or streamed; otherwise calls <paramref name="start"/> with the conversation id once, streams to
    /// the writer it returns, and returns null.
    /// </summary>
    Task<HrChatRefusal?> AskAsync(User user, HrChatRequest req, Func<int, ChatStreamWriter> start, CancellationToken ct);
}

public sealed class HrChatService(AppDbContext db, ChatService chat, HrLibrary library, Rbac.ModelPolicy modelPolicy,
    IConfiguration config, ILoggerFactory logs) : IHrChatService
{
    public const int ReservedForAnswer = HrChatEndpoint.ReservedForAnswer;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<HrChatRefusal?> AskAsync(User user, HrChatRequest req, Func<int, ChatStreamWriter> start, CancellationToken ct)
    {
        var log = logs.CreateLogger("HrChat");

            var question = (req.Message ?? "").Trim();
            if (question.Length == 0) return new HrChatRefusal(400, "message is required.", null);

            // ---- 1. the model, through the one policy ----
            var model = string.IsNullOrWhiteSpace(req.Model)
                ? await modelPolicy.DefaultForAsync(user, ct) ?? config["Hr:Model"] ?? "gemma4:12b"
                : req.Model.Trim();
            var policy = await modelPolicy.CheckAsync(user, model, Rbac.ModelPolicy.LocalSource, ct: ct);
            if (!policy.Allowed) return new HrChatRefusal(StatusCodes.Status403Forbidden, policy.Error!, null);

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
                return new HrChatRefusal(StatusCodes.Status422UnprocessableEntity, null, new
                {
                    error = $"The persona and always items use {systemTokens:N0} tokens, more than 8,000. Largest: "
                          + $"{string.Join(", ", largest)}. Make one of them Search, or switch it off, in the Library.",
                });
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
            var writer = start(convo.Id);
            await writer.FrameAsync(new { type = "status", text = HrPrompt.Searching, found = (int?)null });

            // ---- 3. excerpts: the best 5, whole chunks, up to 1,500 tokens ----
            var excerpts = await library.SelectExcerptsAsync(question, req.Items, ct);
            var (_, searchTerms) = await library.ExpandAsync(question, ct);
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
                    chunkId = e.ChunkId, itemId = e.ItemId, name = e.ItemName, version = e.Version, sourceRef = e.SourceRef, cite = e.Cite,
                    rulePageId = e.RulePageId, ruleVersion = e.RuleVersion, pinned = e.Pinned,
                    lineFrom = e.LineFrom, lineTo = e.LineTo, tokens = e.Tokens, score = e.Score,
                }),
                scope = req.Items ?? new List<int>(),
                searchTerms,
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
                await SaveAsync(db, convo.Id, answer.ToString(), receipt, formulas.Formulas, Array.Empty<decimal>(), Array.Empty<string>(), CancellationToken.None, Array.Empty<string>());
                return null;
            }

            // ---- 7. the number check: formula results count as verified ----
            var userText = earlier.Where(m => m.Role == "user").Select(m => m.Content).Append(question);
            var calculated = formulas.Formulas.Where(f => f.Ok).Select(f => f.Value!.Value);
            var unverified = HrNumberCheck.Unverified(answer.ToString(), userText.Concat(new[] { system, excerptBlock }), calculated);
            var failed = formulas.Formulas.Where(f => !f.Ok).Select(f => f.Expression).ToList();
            // Citations that name nothing this answer was sent: "unknown source", listed with the amounts.
            var citations = HrCitations.Unknown(answer.ToString(), excerpts.Select(HrCitable.Of)
                .Concat(always.Select(i => new HrCitable(i.Name, null, Array.Empty<string>(), Whole: true))).ToList());
            await writer.FrameAsync(new { type = "unverified", amounts = unverified, formulas = failed, citations });
            await writer.DoneAsync(false);

            await SaveAsync(db, convo.Id, answer.ToString(), receipt, formulas.Formulas, unverified, failed, ct, citations);
            log.LogInformation("HR chat: {Excerpts} excerpts, {History} history turns ({Dropped} dropped), {Formulas} formulas ({Failed} failed), {Unverified} unverified.",
                excerpts.Count, history.Count, dropped, formulas.Formulas.Count, failed.Count, unverified.Count);
            return null;
        }

        /// <summary>The answer, with what the "Context used" panel, the formulas and the warning line need on reopening.</summary>
        private static async Task SaveAsync(AppDbContext db, int conversationId, string answer, object receipt,
            IReadOnlyList<HrFormula> formulas, IReadOnlyList<decimal> unverified, IReadOnlyList<string> failedFormulas, CancellationToken ct,
            IReadOnlyList<string> unknownCitations)
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
                    unverifiedCitations = unknownCitations,
                }, Json),
            });
            await db.SaveChangesAsync(ct);
        }
}
