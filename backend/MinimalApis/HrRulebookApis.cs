// মানব সম্পদ · নিয়মাবলি — HTTP surface (docs/HR_RULEBOOK_PROMPT.md §3.1–§3.3, §3.6).
//
//   GET  /api/hr/rulebook?q=…&category=…&status=…   the pages the caller may see
//   POST /api/hr/rulebook                          a new page, v1 `written`            (editors)
//   GET  /api/hr/rulebook/export                   every confirmed page, one Markdown file
//   GET  /api/hr/rulebook/{id}?version=N           a page at a version (default: the one the caller opens)
//   PUT  /api/hr/rulebook/{id}                     save: a new `edited` version        (editors)
//   GET  /api/hr/rulebook/{id}/versions            the history
//   GET  /api/hr/rulebook/{id}/download?version=N  a version as Markdown, under its header line
//   POST /api/hr/rulebook/{id}/confirm             { note } — required                 (editors)
//   POST /api/hr/rulebook/{id}/retire              { note } — required                 (editors)
//   POST /api/hr/rulebook/{id}/restore             { version, note } — note required   (editors)
//   GET  /api/hr/rulebook/{id}/check?version=N     the number check before Confirm     (editors)
//   POST /api/hr/rulebook/generate                 a draft from the Library, streamed  (editors)
//
// There is no route that changes or removes a version, and none that deletes a
// page: the history is append-only (§0 rule 4). /api/hr is gated by the `hr` tab;
// who may write, and who may see a draft, is decided in HrRulebook.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Extraction;
using RabbitIC.Api.Hr;
using RabbitIC.Api.ModelSources;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.MinimalApis
{
    public static class HrRulebookApis
    {
        public static IEndpointRouteBuilder MapHrRulebookApis(this IEndpointRouteBuilder app)
        {
            app.MapGet("/hr/rulebook", async (string? q, string? category, string? status, HttpContext ctx, AppDbContext db, HrRulebook rulebook, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return Results.Ok(new
                {
                    pages = await rulebook.ListAsync(user, q, category, status, ct),
                    canEdit = HrRulebook.CanEdit(user),
                    categories = HrRulebook.Categories,
                    languages = HrRulebook.Languages,
                    statuses = HrRulebook.Statuses,
                });
            });

            app.MapPost("/hr/rulebook", async (HrRulePageInput input, HttpContext ctx, AppDbContext db, HrRulebook rulebook, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                var result = await rulebook.CreateAsync(user, input, ct);
                return result.Outcome == HrRuleOutcome.Ok
                    ? Results.Created($"/api/hr/rulebook/{result.Page!.Id}", result.Page)
                    : Answer(result);
            });

            app.MapGet("/hr/rulebook/export", async (HttpContext ctx, AppDbContext db, HrRulebook rulebook, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                var text = await rulebook.ExportConfirmedAsync(ct);
                return Results.File(Encoding.UTF8.GetBytes(text), "text/markdown; charset=utf-8", $"rulebook-confirmed-{DateTime.UtcNow:yyyy-MM-dd}.md");
            });

            app.MapGet("/hr/rulebook/{id:int}", async (int id, int? version, HttpContext ctx, AppDbContext db, HrRulebook rulebook, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return Answer(await rulebook.GetAsync(user, id, version, ct));
            });

            app.MapPut("/hr/rulebook/{id:int}", async (int id, HrRulePageInput input, HttpContext ctx, AppDbContext db, HrRulebook rulebook, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return Answer(await rulebook.SaveAsync(user, id, input, ct));
            });

            app.MapGet("/hr/rulebook/{id:int}/versions", async (int id, HttpContext ctx, AppDbContext db, HrRulebook rulebook, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                var (outcome, versions) = await rulebook.VersionsAsync(user, id, ct);
                return outcome switch
                {
                    HrRuleOutcome.Ok => Results.Ok(new { versions }),
                    HrRuleOutcome.Forbidden => Error(HrRulebook.DraftsHidden, StatusCodes.Status403Forbidden),
                    _ => Error("No such Rulebook page.", StatusCodes.Status404NotFound),
                };
            });

            app.MapGet("/hr/rulebook/{id:int}/download", async (int id, int? version, HttpContext ctx, AppDbContext db, HrRulebook rulebook, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                var (result, fileName, text) = await rulebook.DownloadAsync(user, id, version, ct);
                return result.Outcome == HrRuleOutcome.Ok
                    ? Results.File(Encoding.UTF8.GetBytes(text!), "text/markdown; charset=utf-8", fileName)
                    : Answer(result);
            });

            app.MapPost("/hr/rulebook/{id:int}/confirm", async (int id, HrRuleNoteInput input, HttpContext ctx, AppDbContext db, HrRulebook rulebook, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return Answer(await rulebook.ConfirmAsync(user, id, input.Note, ct));
            });

            app.MapPost("/hr/rulebook/{id:int}/retire", async (int id, HrRuleNoteInput input, HttpContext ctx, AppDbContext db, HrRulebook rulebook, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return Answer(await rulebook.RetireAsync(user, id, input.Note, ct));
            });

            app.MapPost("/hr/rulebook/{id:int}/restore", async (int id, HrRuleRestoreInput input, HttpContext ctx, AppDbContext db, HrRulebook rulebook, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return Answer(await rulebook.RestoreAsync(user, id, input.Version, input.Note, ct));
            });

            // The number check before Confirm (§3.4).
            app.MapGet("/hr/rulebook/{id:int}/check", async (int id, int? version, HttpContext ctx, AppDbContext db, HrRulebook rulebook, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                var (outcome, check) = await rulebook.CheckAsync(user, id, version, ct);
                return outcome switch
                {
                    HrRuleOutcome.Ok => Results.Ok(check),
                    HrRuleOutcome.Forbidden => Error(HrRulebook.EditorsOnly, StatusCodes.Status403Forbidden),
                    _ => Error("No such Rulebook page or version.", StatusCodes.Status404NotFound),
                };
            });

            app.MapPost("/hr/rulebook/generate", GenerateAsync);

            return app;
        }

        /// <summary>What a draft may use of the context window besides the system message and the excerpts.</summary>
        public const int ReservedForDraft = 3000;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>
        /// POST /api/hr/rulebook/generate — a draft from the Library (§3.4), streamed
        /// as the HR chat streams: "status" (before retrieval, and with the count),
        /// "context" (what is sent), "content" (the model's words as they come),
        /// then "draft" — the page as saved: the template kept (HrRuleDraft.Shape),
        /// a `generated` version with its sources and model details, and the amounts
        /// the number check could not trace. Editors only; the model through
        /// ModelPolicy. Never confirmed: the page stays a draft.
        /// </summary>
        public static async Task GenerateAsync(
            HrRuleGenerateRequest req, HttpContext ctx, AppDbContext db, ChatService chat, HrLibrary library, HrRulebook rulebook,
            Rbac.ModelPolicy modelPolicy, IConfiguration config, ILoggerFactory logs)
        {
            var log = logs.CreateLogger("HrRulebookDraft");
            var ct = ctx.RequestAborted;
            var clock = Stopwatch.StartNew();

            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("Unauthorized."); return; }

            var (refusal, target) = await rulebook.ValidateDraftAsync(user, req, ct);
            if (refusal is not null) { await Answer(refusal).ExecuteAsync(ctx); return; }

            var model = string.IsNullOrWhiteSpace(req.Model)
                ? await modelPolicy.DefaultForAsync(user, ct) ?? config["Hr:Model"] ?? "gemma4:12b"
                : req.Model.Trim();
            var policy = await modelPolicy.CheckAsync(user, model, Rbac.ModelPolicy.LocalSource, ct: ct);
            if (!policy.Allowed) { await Error(policy.Error, StatusCodes.Status403Forbidden).ExecuteAsync(ctx); return; }

            var window = int.TryParse(config["Hr:NumCtx"], out var w) && w > 0 ? w : 16384;
            var keepAlive = int.TryParse(config["Hr:KeepAlive"], out var k) ? k : -1;
            var temperature = double.TryParse(config["Hr:Temperature"], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var tv) ? tv : 0;

            var always = await library.AlwaysItemsAsync(ct);
            var system = HrRuleDraft.System(always);
            var systemTokens = PoPromptBuilder.EstimateTokens(system);
            if (systemTokens + HrLibrary.DraftExcerptBudget + ReservedForDraft > window)
            {
                await Error($"The drafting instructions and always items use {systemTokens:N0} tokens; with 5,000 for excerpts and {ReservedForDraft:N0} for the draft "
                    + $"that is more than the {window:N0}-token window. Make an always item Search in the Library.", StatusCodes.Status422UnprocessableEntity).ExecuteAsync(ctx);
                return;
            }

            ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
            var writer = new ChatStreamWriter(ctx.Response, ct);
            await writer.FrameAsync(new { type = "status", text = HrPrompt.Searching, found = (int?)null });

            var query = HrRuleDraft.Query(target!.Title, req.Instruction);
            var excerpts = await library.SelectDraftExcerptsAsync(query, req.Items, ct);
            await writer.FrameAsync(new { type = "status", text = HrPrompt.Found(excerpts.Count), found = (int?)excerpts.Count });

            var request = HrRuleDraft.Request(excerpts, target.Title, target.Category, target.Language, req.Instruction);
            var excerptTokens = excerpts.Sum(e => e.Tokens);
            await writer.FrameAsync(new
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
                tokens = new { system = systemTokens, excerpts = excerptTokens, request = PoPromptBuilder.EstimateTokens(request), window, reserved = ReservedForDraft },
            });

            var turns = new List<ChatTurn> { new("system", system), new("user", request) };
            var capabilities = await ChatEndpoint.ModelCapabilitiesAsync(chat, Rbac.ModelPolicy.LocalSource, model, ct);
            bool? think = capabilities.Any(c => c.Equals("thinking", StringComparison.OrdinalIgnoreCase)
                                             || c.Equals("reasoning", StringComparison.OrdinalIgnoreCase)) ? false : null;
            var args = new ChatArgs(model, turns, Tools: null, think, window, keepAlive, temperature);

            var answer = new StringBuilder();
            long? firstTokenMs = null;
            try
            {
                await foreach (var e in chat.StreamAsync(Rbac.ModelPolicy.LocalSource, args, ct))
                {
                    if (e.Kind != ChatEventKind.Content || string.IsNullOrEmpty(e.Text)) continue;
                    firstTokenMs ??= clock.ElapsedMilliseconds;
                    answer.Append(e.Text);
                    await writer.ContentAsync(e.Text);
                }
            }
            catch (ChatSourceException ex)
            {
                // Nothing is saved: half a draft is not a version.
                await writer.ErrorAsync(ex.Message);
                await writer.DoneAsync(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // The template's promises kept, then the number check against what the draft was written from.
            var body = HrRuleDraft.Shape(answer.ToString(), target.Title, target.Category, excerpts, always);
            var known = HrRuleDraft.KnownAmounts(always.Select(i => i.Content)
                .Append(HrRuleDraft.Excerpts(excerpts)).Append(target.Title).Append(req.Instruction ?? ""));
            var (unverified, failed) = HrRuleDraft.Check(body, known);

            var sources = HrRulebook.WriteSources(new HrRulebook.DraftSources(
                query, req.Items ?? new List<int>(),
                always.Select(i => new HrRulebook.DraftAlways(i.Id, i.Name, i.Version)).ToList(),
                excerpts.Select(e => new HrRulebook.DraftExcerpt(e.ChunkId, e.ItemId, e.ItemName, e.Version, e.SourceRef, e.LineFrom, e.LineTo, e.Tokens, e.Score)).ToList(),
                known));
            var modelJson = JsonSerializer.Serialize(new
            {
                model,
                promptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(system + "\n\u0000\n" + request))).ToLowerInvariant(),
                tokens = new { system = systemTokens, excerpts = excerptTokens, request = PoPromptBuilder.EstimateTokens(request), output = PoPromptBuilder.EstimateTokens(answer.ToString()), estimated = true },
                firstTokenMs,
                durationMs = clock.ElapsedMilliseconds,
                temperature,
                unverified,
                failedFormulas = failed,
            }, Json);

            var saved = await rulebook.SaveGeneratedAsync(user, target, body, sources, modelJson, $"Drafted from the Library by {model}", CancellationToken.None);
            if (saved.Outcome != HrRuleOutcome.Ok)
            {
                await writer.ErrorAsync(saved.Error ?? "The draft could not be saved.");
                await writer.DoneAsync(false);
                return;
            }

            await writer.FrameAsync(new
            {
                type = "draft", pageId = saved.Page!.Id, version = saved.Page.CurrentVersion, body,
                unverified, formulas = failed, firstTokenMs, durationMs = clock.ElapsedMilliseconds,
            });
            await writer.DoneAsync(false);
            log.LogInformation("Rulebook draft: page {Page} v{Version}, {Excerpts} excerpts, {Unverified} unverified, {Ms} ms.",
                saved.Page.Id, saved.Page.CurrentVersion, excerpts.Count, unverified.Count, clock.ElapsedMilliseconds);
        }

        private static IResult Answer(HrRuleResult result) => result.Outcome switch
        {
            HrRuleOutcome.Ok => Results.Ok(result.Page),
            HrRuleOutcome.Forbidden => Error(result.Error, StatusCodes.Status403Forbidden),
            HrRuleOutcome.Invalid => Error(result.Error, StatusCodes.Status400BadRequest),
            HrRuleOutcome.Conflict => Error(result.Error, StatusCodes.Status409Conflict),
            _ => Error(result.Error, StatusCodes.Status404NotFound),
        };

        // Not Results.Forbid(): it needs an authentication scheme this app has none of.
        private static IResult Error(string? error, int status) => Results.Json(new { error }, statusCode: status);
    }
}
