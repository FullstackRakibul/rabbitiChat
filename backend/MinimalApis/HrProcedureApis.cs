// মানব সম্পদ · প্রসিডিউর — HTTP surface (docs/HR_PROCEDURE_ANALYZER_PROMPT.md §3.1–§3.4, §3.7).
//
//   GET  /api/hr/procedures                     the uploaded procedures
//   POST /api/hr/procedures                     multipart: file (.sql, ≤ 2 MB), note     (DevOps, SystemAdmin)
//   GET  /api/hr/procedures/{id}?version=N      a version: its text, facts and the version list
//   GET  /api/hr/procedures/{id}/setup          the setup list of the current version
//   GET  /api/hr/procedures/{id}/setup.md       the same, as Markdown
//   GET  /api/hr/questions?status=…&procedureId=…   open (default), answered, not-business or all
//   POST /api/hr/questions/{id}/answer          { label?, note?, notBusiness? }          (DevOps, hr-assistant, SystemAdmin)
//   GET  /api/hr/lookups?q=…&column=…           the registry
//   PUT  /api/hr/lookups                        { column, value, label, note }           (DevOps, SystemAdmin)
//   GET  /api/hr/lookups/log?column=…&value=…   the registry's change log
//
// The SQL is text: nothing here connects to a database server or runs it. There
// is no route that changes or removes a version or a log row. /api/hr is gated
// by the `hr` tab; who may write is decided in HrProcedures, through AccessPolicy.

using System.Text;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Hr;

namespace RabbitIC.Api.MinimalApis
{
    public static class HrProcedureApis
    {
        public static IEndpointRouteBuilder MapHrProcedureApis(this IEndpointRouteBuilder app)
        {
            app.MapGet("/hr/procedures", async (HttpContext ctx, AppDbContext db, HrProcedures procedures, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return Results.Ok(new
                {
                    procedures = await procedures.ListAsync(ct),
                    canUpload = await procedures.CanUploadAsync(user, ct),
                    canAnswer = await procedures.CanAnswerAsync(user, ct),
                    canEditRegistry = await procedures.CanEditRegistryAsync(user, ct),
                    maxUploadBytes = HrProcedures.MaxUploadBytes,
                });
            });

            app.MapPost("/hr/procedures", async (HttpContext ctx, AppDbContext db, HrProcedures procedures, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                if (!await procedures.CanUploadAsync(user, ct)) return Error(HrProcOutcome.Forbidden, "Only DevOps and SystemAdmin users can upload procedures.");
                if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "Send the file as multipart/form-data." });
                var form = await ctx.Request.ReadFormAsync(ct);
                if (form.Files.Count != 1) return Results.BadRequest(new { error = "Send one .sql file." });

                var result = await procedures.UploadAsync(user, form.Files[0], form["note"].ToString(), ct);
                return result.Outcome == HrProcOutcome.Ok
                    ? Results.Created($"/api/hr/procedures/{result.Value.Procedure.Id}", new { procedure = result.Value.Procedure, version = result.Value.Version })
                    : Error(result.Outcome, result.Error);
            }).DisableAntiforgery();

            app.MapGet("/hr/procedures/{id:int}", async (int id, int? version, HttpContext ctx, AppDbContext db, HrProcedures procedures, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return await procedures.GetAsync(id, version, ct) is { } found ? Results.Ok(found) : Results.NotFound(new { error = "No such procedure or version." });
            });

            app.MapGet("/hr/procedures/{id:int}/setup", async (int id, HttpContext ctx, AppDbContext db, HrProcedures procedures, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                if (await procedures.SetupAsync(id, ct) is not { } setup) return Results.NotFound(new { error = "No such procedure." });
                return Results.Ok(new
                {
                    objectName = setup.Procedure.ObjectName, version = setup.Version.Version, uploadedBy = setup.UploadedBy, uploadedAtUtc = setup.Version.AtUtc,
                    groups = HrProcedures.Groups.Select(g => new { key = g.Key, bn = g.Bn, en = g.En }),
                    items = setup.Items,
                });
            });

            app.MapGet("/hr/procedures/{id:int}/setup.md", async (int id, HttpContext ctx, AppDbContext db, HrProcedures procedures, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                if (await procedures.SetupAsync(id, ct) is not { } setup) return Results.NotFound(new { error = "No such procedure." });
                var text = HrProcedures.SetupMarkdown(setup.Procedure, setup.Version, setup.UploadedBy, setup.Items, DateTime.UtcNow);
                return Results.File(Encoding.UTF8.GetBytes(text), "text/markdown; charset=utf-8",
                    $"{setup.Procedure.ObjectName}-v{setup.Version.Version}-setup-list.md");
            });

            app.MapGet("/hr/questions", async (string? status, int? procedureId, HttpContext ctx, AppDbContext db, HrProcedures procedures, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return Results.Ok(new { questions = await procedures.QuestionsAsync(status, procedureId, ct), canAnswer = await procedures.CanAnswerAsync(user, ct) });
            });

            app.MapPost("/hr/questions/{id:int}/answer", async (int id, HrAnswerInput input, HttpContext ctx, AppDbContext db, HrProcedures procedures, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                var result = await procedures.AnswerAsync(user, id, input, ct);
                return result.Outcome == HrProcOutcome.Ok ? Results.Ok(result.Value) : Error(result.Outcome, result.Error);
            });

            app.MapGet("/hr/lookups", async (string? q, string? column, HttpContext ctx, AppDbContext db, HrProcedures procedures, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return Results.Ok(new
                {
                    lookups = await procedures.LookupsAsync(q, column, ct),
                    columns = await procedures.ColumnsAsync(ct),
                    canEdit = await procedures.CanEditRegistryAsync(user, ct),
                });
            });

            app.MapPut("/hr/lookups", async (HrLookupInput input, HttpContext ctx, AppDbContext db, HrProcedures procedures, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                var result = await procedures.EditLookupAsync(user, input, ct);
                return result.Outcome == HrProcOutcome.Ok ? Results.Ok(result.Value) : Error(result.Outcome, result.Error);
            });

            app.MapGet("/hr/lookups/log", async (string? column, string? value, HttpContext ctx, AppDbContext db, HrProcedures procedures, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return Results.Ok(new { log = await procedures.LookupLogAsync(column, value, ct) });
            });

            return app;
        }

        private static IResult Error(HrProcOutcome outcome, string? error) => outcome switch
        {
            // Not Results.Forbid(): it needs an authentication scheme this app has none of.
            HrProcOutcome.Forbidden => Results.Json(new { error }, statusCode: StatusCodes.Status403Forbidden),
            HrProcOutcome.NotFound => Results.NotFound(new { error }),
            HrProcOutcome.Conflict => Results.Conflict(new { error }),
            _ => Results.BadRequest(new { error }),
        };
    }
}
