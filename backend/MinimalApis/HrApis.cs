// মানব সম্পদ — HTTP surface (docs/HR_MODULE_PROMPT.md §3.2–§3.4).
//
// Library
//   GET    /api/hr/library                 every item: type, mode, version, approval, size, chunks, indexing state
//   GET    /api/hr/library/{id}            one item with its content
//   GET    /api/hr/library/{id}/chunks     a `search` item's chunks, for the preview
//   POST   /api/hr/library                 add by pasting text or JSON           (HrAssistant, SystemAdmin)
//   POST   /api/hr/library/upload          add one or many files at once         (HrAssistant, SystemAdmin)
//   PUT    /api/hr/library/{id}            edit, switch mode, approve            (HrAssistant, SystemAdmin)
//   DELETE /api/hr/library/{id}                                                  (HrAssistant, SystemAdmin)
//   POST   /api/hr/library/{id}/retry      re-queue an item whose embedding failed (editors)
//   POST   /api/hr/library/embed           re-queue every item with un-embedded chunks (editors)
//   (A Rulebook page's Library copy answers PUT and DELETE with 409: it is edited in the Rulebook.)
//   GET    /api/hr/library/index-stats     chunks in memory / database / keyword index (action:hr.library.stats)
// Ask
//   GET    /api/hr/search?q=…&items=…      instant hybrid search, no model
//   GET    /api/hr/retrieve?q=…            the excerpts a question would be sent with
//   POST   /api/hr/chat                    the HR chat, streamed (HrChatEndpoint)
//   POST   /api/hr/calculate               the formula evaluator on its own; { inline: true } as a [[= … ]] is (the Rulebook preview)
//   GET    /api/hr/conversations           this user's HR conversations
//   GET    /api/hr/conversations/{id}      one, with each answer's receipt
//   DELETE /api/hr/conversations/{id}
//
// The whole of /api/hr is gated by the `hr` tab in TabAccessMiddleware, which is
// what "anyone with the hr tab can read" means. Who may WRITE is decided once,
// in ContextSourceService (action:hr.library.edit); these routes only translate its outcome.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Hr;
using RabbitIC.Api.Models;
using RabbitIC.Api.Rbac;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.MinimalApis
{
    /// <param name="Inline">Work it out as an inline [[= … ]] formula is (HrFormulas.EvaluateInline): Bengali digits, ×, ৳ and separators allowed, the result as ৳. Always 200, with ok and the reason.</param>
    public record HrCalculateRequest(string? Expression, bool? Inline = null);

    public static class HrApis
    {
        public static IEndpointRouteBuilder MapHrApis(this IEndpointRouteBuilder app)
        {
            // ───────────────────────── library ─────────────────────────

            app.MapGet("/hr/library", async (HttpContext ctx, AppDbContext db, HrLibrary library, IContextSourceService sources, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return Results.Ok(new
                {
                    items = await library.ListAsync(user, ct),
                    canEdit = await sources.CanEditSharedAsync(user, ct),
                    alwaysTokens = await library.AlwaysTokensAsync(ct),
                    alwaysCap = HrLibrary.AlwaysCapTokens,
                    types = HrLibrary.Types,
                    modes = HrLibrary.Modes,
                    uploadExtensions = HrLibrary.UploadExtensions,
                });
            });

            app.MapGet("/hr/library/{id:int}", async (int id, HttpContext ctx, AppDbContext db, HrLibrary library, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                var item = await library.GetAsync(user, id, ct);
                return item is null ? Results.NotFound() : Results.Ok(item);
            });

            app.MapGet("/hr/library/{id:int}/chunks", async (int id, HttpContext ctx, AppDbContext db, HrLibrary library, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                var chunks = await library.ChunksAsync(id, ct);
                return chunks is null ? Results.NotFound() : Results.Ok(new { chunks });
            });

            app.MapPost("/hr/library", async (HrItemInput input, HttpContext ctx, AppDbContext db, HrLibrary library, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                var (outcome, item, error) = await library.CreateAsync(user, input, ct);
                if (error is not null) return Results.BadRequest(new { error });
                return outcome == SharedOutcome.Forbidden ? Forbidden() : Results.Created($"/api/hr/library/{item!.Id}", item);
            });

            // One or many files in one request; each answered on its own, so one bad
            // file does not lose the rest. 201 when any was added, 400 when none.
            app.MapPost("/hr/library/upload", async (HttpContext ctx, AppDbContext db, HrLibrary library, IContextSourceService sources, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                if (!await sources.CanEditSharedAsync(user, ct)) return Forbidden();
                if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "Send the files as multipart/form-data." });

                var form = await ctx.Request.ReadFormAsync(ct);
                if (form.Files.Count == 0) return Results.BadRequest(new { error = "No file was sent." });

                var results = new List<object>();
                var added = 0;
                foreach (var file in form.Files)
                {
                    var (text, readError) = await HrLibrary.ReadUploadAsync(file, ct);
                    if (readError is not null) { results.Add(new { file = file.FileName, ok = false, error = readError }); continue; }

                    var (name, type) = HrLibrary.Describe(Path.GetFileName(file.FileName));
                    var single = form.Files.Count == 1;
                    var input = new HrItemInput(single ? Blank(form["name"]) ?? name : name, Blank(form["hrType"]) ?? type,
                        Blank(form["mode"]), text, null, Blank(form["approvedBy"]));
                    var (outcome, item, error) = await library.CreateAsync(user, input, ct);
                    if (error is not null || outcome != SharedOutcome.Ok)
                    {
                        results.Add(new { file = file.FileName, ok = false, error = error ?? "Not allowed." });
                        continue;
                    }
                    added++;
                    results.Add(new { file = file.FileName, ok = true, item });
                }
                return Results.Json(new { results }, statusCode: added > 0 ? 201 : 400);
            }).DisableAntiforgery();

            app.MapPut("/hr/library/{id:int}", async (int id, HrItemInput input, HttpContext ctx, AppDbContext db, HrLibrary library, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                var (outcome, item, error) = await library.UpdateAsync(user, id, input, ct);
                if (error is not null) return Results.BadRequest(new { error });
                return outcome switch
                {
                    SharedOutcome.Ok => Results.Ok(item),
                    SharedOutcome.Forbidden => Forbidden(),
                    SharedOutcome.Conflict => RulebookOwned(),
                    _ => Results.NotFound(),
                };
            });

            app.MapDelete("/hr/library/{id:int}", async (int id, HttpContext ctx, AppDbContext db, HrLibrary library, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return await library.DeleteAsync(user, id, ct) switch
                {
                    SharedOutcome.Ok => Results.NoContent(),
                    SharedOutcome.Forbidden => Forbidden(),
                    SharedOutcome.Conflict => RulebookOwned(),
                    _ => Results.NotFound(),
                };
            });

            app.MapPost("/hr/library/{id:int}/retry", async (int id, HttpContext ctx, AppDbContext db, HrLibrary library, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return await library.RetryAsync(user, id, ct) switch
                {
                    SharedOutcome.Ok => Results.Ok(new { queued = id }),
                    SharedOutcome.Forbidden => Forbidden(),
                    _ => Results.NotFound(),
                };
            });

            app.MapPost("/hr/library/embed", async (HttpContext ctx, AppDbContext db, HrLibrary library, IContextSourceService sources, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                if (!await sources.CanEditSharedAsync(user, ct)) return Forbidden();
                var queued = 0;
                foreach (var item in await library.ListAsync(user, ct))
                    if (item.EmbeddedCount < item.ChunkCount && await library.RetryAsync(user, item.Id, ct) == SharedOutcome.Ok) queued++;
                return Results.Ok(new { queued });
            });

            app.MapGet("/hr/library/index-stats", async (HttpContext ctx, AppDbContext db, HrLibrary library, AccessPolicy access, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                if (!await access.HasAsync(user, PermissionKeys.HrLibraryStats, ct)) return Results.Json(new { error = "SystemAdmin role required." }, statusCode: 403);
                return Results.Ok(await library.IndexStatsAsync(ct));
            });

            // ───────────────────────── ask ─────────────────────────

            app.MapGet("/hr/search", async (string? q, string? items, HttpContext ctx, AppDbContext db, HrLibrary library, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest(new { error = "q is required." });
                var clock = Stopwatch.StartNew();
                var hits = await library.SearchAsync(q, ParseIds(items), 10, ct);
                return Results.Ok(new { hits, tookMs = clock.ElapsedMilliseconds });
            });

            app.MapGet("/hr/retrieve", async (string? q, HttpContext ctx, AppDbContext db, HrLibrary library, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest(new { error = "q is required." });
                return Results.Ok(new { hits = await library.RetrieveAsync(q, ct) });
            });

            app.MapPost("/hr/chat", HrChatEndpoint.HandleAsync);

            app.MapPost("/hr/calculate", async (HrCalculateRequest req, HttpContext ctx, AppDbContext db) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                if (req.Inline == true)
                {
                    var (inlineValue, display, inlineError) = HrFormulas.EvaluateInline(req.Expression);
                    return Results.Ok(new
                    {
                        expression = req.Expression, ok = inlineValue is not null,
                        result = inlineValue is decimal r ? HrCalculator.Format(r) : null, display, error = inlineError,
                    });
                }
                var (value, error) = HrCalculator.Evaluate(req.Expression);
                return value is null
                    ? Results.BadRequest(new { error })
                    : Results.Ok(new { expression = req.Expression, result = HrCalculator.Format(value.Value) });
            });

            app.MapGet("/hr/conversations", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return Results.Ok(await db.Conversations.AsNoTracking()
                    .Where(c => c.UserId == user.Id && c.Kind == ContextSource.KindHr)
                    .OrderByDescending(c => c.Id)
                    .Select(c => new { c.Id, c.Title, c.CreatedAt })
                    .ToListAsync(ct));
            });

            app.MapGet("/hr/conversations/{id:int}", async (int id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                var convo = await db.Conversations.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == id && c.UserId == user.Id && c.Kind == ContextSource.KindHr, ct);
                if (convo is null) return Results.NotFound();
                var messages = await db.Messages.AsNoTracking().Where(m => m.ConversationId == id).OrderBy(m => m.Id)
                    .Select(m => new { m.Id, m.Role, m.Content, m.HrMetaJson }).ToListAsync(ct);
                return Results.Ok(new
                {
                    convo.Id, convo.Title, convo.CreatedAt,
                    messages = messages.Select(m => new
                    {
                        m.Id, m.Role, m.Content,
                        meta = m.HrMetaJson is null ? (JsonElement?)null : JsonDocument.Parse(m.HrMetaJson).RootElement.Clone(),
                    }),
                });
            });

            app.MapDelete("/hr/conversations/{id:int}", async (int id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                var convo = await db.Conversations.FirstOrDefaultAsync(c => c.Id == id && c.UserId == user.Id && c.Kind == ContextSource.KindHr, ct);
                if (convo is null) return Results.NotFound();
                db.Conversations.Remove(convo);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            });

            return app;
        }

        // Not Results.Forbid(): it needs an authentication scheme this app has none of.
        private static IResult Forbidden() =>
            Results.Json(new { error = "Only hr-assistant and SystemAdmin users can change the HR library." }, statusCode: 403);

        /// <summary>A Rulebook page's Library copy: changed only from the Rulebook (docs/HR_RULEBOOK_PROMPT.md §3.3).</summary>
        private static IResult RulebookOwned() =>
            Results.Json(new { error = HrLibrary.RulebookOwned }, statusCode: StatusCodes.Status409Conflict);

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static List<int>? ParseIds(string? csv) =>
            string.IsNullOrWhiteSpace(csv)
                ? null
                : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).ToList();
    }
}
