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
//
// There is no route that changes or removes a version, and none that deletes a
// page: the history is append-only (§0 rule 4). /api/hr is gated by the `hr` tab;
// who may write, and who may see a draft, is decided in HrRulebook.

using System.Text;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Hr;

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

            return app;
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
