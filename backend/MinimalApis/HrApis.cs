// মানব সম্পদ — HTTP surface for the HR library (docs/HR_MODULE_PROMPT.md §3.2, §3.4).
//
//   GET    /api/hr/library                 every item, with type, mode, version, approval, size, chunks
//   GET    /api/hr/library/{id}            one item with its content
//   GET    /api/hr/library/{id}/chunks     a `search` item's chunks, for the preview
//   POST   /api/hr/library                 add by pasting text           (HrAssistant, SystemAdmin)
//   POST   /api/hr/library/upload          add a .md/.txt/.sql/.csv file (HrAssistant, SystemAdmin)
//   PUT    /api/hr/library/{id}            edit, switch mode, approve    (HrAssistant, SystemAdmin)
//   DELETE /api/hr/library/{id}                                          (HrAssistant, SystemAdmin)
//   POST   /api/hr/library/embed           retry chunks Ollama could not embed earlier (editors)
//   GET    /api/hr/retrieve?q=…            the best excerpts for a question
//   GET    /api/hr/library/index-stats     chunks in memory vs in the database (SystemAdmin)
//
// The whole of /api/hr is gated by the `hr` tab in TabAccessMiddleware, which is
// what "anyone with the hr tab can read" means. Who may WRITE is decided once,
// in ContextSourceService; these routes only translate its outcome.

using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Hr;
using RabbitIC.Api.Models;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.MinimalApis
{
    public static class HrApis
    {
        public static IEndpointRouteBuilder MapHrApis(this IEndpointRouteBuilder app)
        {
            app.MapGet("/hr/library", async (HttpContext ctx, AppDbContext db, HrLibrary library, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return Results.Ok(new
                {
                    items = await library.ListAsync(user, ct),
                    canEdit = ContextSourceService.CanEditShared(user),
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
                return Created(await library.CreateAsync(user, input, ct));
            });

            app.MapPost("/hr/library/upload", async (HttpContext ctx, AppDbContext db, HrLibrary library, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                if (!ContextSourceService.CanEditShared(user)) return Forbidden();
                if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "Send the file as multipart/form-data." });

                var form = await ctx.Request.ReadFormAsync(ct);
                var file = form.Files.GetFile("file");
                if (file is null) return Results.BadRequest(new { error = "No file was sent." });

                var (text, error) = await HrLibrary.ReadUploadAsync(file, ct);
                if (error is not null) return Results.BadRequest(new { error });

                var (name, type) = HrLibrary.Describe(Path.GetFileName(file.FileName));
                var input = new HrItemInput(
                    Blank(form["name"]) ?? name, Blank(form["hrType"]) ?? type, Blank(form["mode"]), text, null, Blank(form["approvedBy"]));
                return Created(await library.CreateAsync(user, input, ct));
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
                    _ => Results.NotFound(),
                };
            });

            app.MapPost("/hr/library/embed", async (HttpContext ctx, AppDbContext db, HrLibrary library, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                if (!ContextSourceService.CanEditShared(user)) return Forbidden();
                return Results.Ok(new { embedded = await library.EmbedPendingAsync(null, ct) });
            });

            app.MapGet("/hr/retrieve", async (string? q, int? top, HttpContext ctx, AppDbContext db, HrLibrary library, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest(new { error = "q is required." });
                return Results.Ok(new { hits = await library.RetrieveAsync(q, Math.Clamp(top ?? 8, 1, 20), ct) });
            });

            app.MapGet("/hr/library/index-stats", async (HttpContext ctx, AppDbContext db, HrLibrary library, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                if (!UserRoles.IsSystemAdmin(user)) return Results.Json(new { error = "SystemAdmin role required." }, statusCode: 403);
                return Results.Ok(await library.IndexStatsAsync(ct));
            });

            return app;
        }

        private static IResult Created((SharedOutcome Outcome, HrItemDto? Item, string? Error) r) =>
            r.Error is not null ? Results.BadRequest(new { error = r.Error })
            : r.Outcome == SharedOutcome.Forbidden ? Forbidden()
            : Results.Created($"/api/hr/library/{r.Item!.Id}", r.Item);

        // Not Results.Forbid(): it needs an authentication scheme this app has none of.
        private static IResult Forbidden() =>
            Results.Json(new { error = "Only hr-assistant and SystemAdmin users can change the HR library." }, statusCode: 403);

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
