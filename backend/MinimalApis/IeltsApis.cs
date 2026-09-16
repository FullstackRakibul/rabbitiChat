using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.MinimalApis
{
    /// <summary>Body of POST /ielts/mistakes/{id}/reviewed.</summary>
    public record MistakeReviewedRequest(bool? Resolved);

    /// <summary>Body of POST /ielts/mistakes/{id}/tag — the user's own diagnosis.</summary>
    public record MistakeTagRequest(string? MistakeType, int? Severity);

    /// <summary>Body of POST /ielts/reading/attempts/{id}/submit.</summary>
    public record SubmitAttemptRequest(List<ReadingAnswerInput>? Answers, int? DurationSeconds);

    /// <summary>
    /// HTTP surface for the IELTS module.
    ///
    /// Holds no rules of its own: ownership, scoring and scheduling all live in
    /// the services, so this file is a routing table. Every handler resolves the
    /// caller and passes only that user's id downward, and a null from a service
    /// becomes 404 — absent and foreign are deliberately the same answer, since a
    /// 403 would confirm that someone else's id exists.
    ///
    /// The whole surface sits behind the "ielts" tab gate in TabAccessMiddleware,
    /// so a user without the tab is refused before any handler runs.
    /// </summary>
    public static class IeltsApis
    {
        /// <summary>
        /// The one place a write outcome becomes a status code. 404 preserves the
        /// existing contract for user content, where absent and foreign must look
        /// alike; 409 says the target exists and is deliberately not writable.
        /// </summary>
        private static IResult MapWrite<T>(ReadingWriteResult<T> result, Func<T, IResult> onOk) =>
            result.Outcome switch
            {
                ReadingWriteOutcome.Ok => onOk(result.Value!),
                ReadingWriteOutcome.ReadOnly => Results.Conflict(new
                {
                    error = "This is shared demo content and is read-only. "
                          + "Copy it into a test of your own to make changes.",
                }),
                _ => Results.NotFound(),
            };

        public static IEndpointRouteBuilder MapIeltsApis(this IEndpointRouteBuilder app)
        {
            MapMistakeApis(app);
            MapReadingApis(app);
            return app;
        }

        // ================================================================
        // Phase 0 — the mistake spine
        // ================================================================
        private static void MapMistakeApis(IEndpointRouteBuilder app)
        {
            // GET /api/ielts/mistakes?module=&type=&due=true
            app.MapGet("/ielts/mistakes", async (
                string? module, string? type, bool? due,
                HttpContext ctx, AppDbContext db, IMistakeService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return Results.Ok(await service.ListAsync(user.Id, module, type, due == true, ct));
            });

            // GET /api/ielts/mistakes/summary — counts by question type.
            app.MapGet("/ielts/mistakes/summary", async (
                string? module,
                HttpContext ctx, AppDbContext db, IMistakeService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return Results.Ok(await service.SummaryAsync(user.Id, module, ct));
            });

            // POST /api/ielts/mistakes/{id}/reviewed — push the next review out,
            // and optionally close the row.
            app.MapPost("/ielts/mistakes/{id:int}/reviewed", async (
                int id, MistakeReviewedRequest? body,
                HttpContext ctx, AppDbContext db, IMistakeService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var dto = await service.MarkReviewedAsync(user.Id, id, body?.Resolved, ct);
                return dto is null ? Results.NotFound() : Results.Ok(dto);
            });

            // POST /api/ielts/mistakes/{id}/tag — why it went wrong. Only a person
            // can say, so nothing here guesses.
            app.MapPost("/ielts/mistakes/{id:int}/tag", async (
                int id, MistakeTagRequest body,
                HttpContext ctx, AppDbContext db, IMistakeService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var dto = await service.TagAsync(user.Id, id, body?.MistakeType, body?.Severity, ct);
                return dto is null ? Results.NotFound() : Results.Ok(dto);
            });
        }

        // ================================================================
        // Phase 1 — the Reading engine
        // ================================================================
        private static void MapReadingApis(IEndpointRouteBuilder app)
        {
            // ---- tests ----

            app.MapGet("/ielts/reading/tests", async (
                HttpContext ctx, AppDbContext db, IReadingService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return Results.Ok(await service.ListTestsAsync(user.Id, ct));
            });

            app.MapGet("/ielts/reading/tests/{id:int}", async (
                int id, HttpContext ctx, AppDbContext db, IReadingService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var dto = await service.GetTestAsync(user.Id, id, ct);
                return dto is null ? Results.NotFound() : Results.Ok(dto);
            });

            app.MapPost("/ielts/reading/tests", async (
                ReadingTestInput input,
                HttpContext ctx, AppDbContext db, IReadingService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                if (string.IsNullOrWhiteSpace(input.Title))
                    return Results.BadRequest(new { error = "title is required." });

                var dto = await service.CreateTestAsync(user.Id, input, ct);
                return Results.Created($"/api/ielts/reading/tests/{dto.Id}", dto);
            });

            app.MapPut("/ielts/reading/tests/{id:int}", async (
                int id, ReadingTestInput input,
                HttpContext ctx, AppDbContext db, IReadingService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return MapWrite(await service.UpdateTestAsync(user.Id, id, input, ct), Results.Ok);
            });

            app.MapDelete("/ielts/reading/tests/{id:int}", async (
                int id, HttpContext ctx, AppDbContext db, IReadingService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return MapWrite(await service.DeleteTestAsync(user.Id, id, ct), _ => Results.NoContent());
            });

            // ---- questions ----

            app.MapPost("/ielts/reading/tests/{id:int}/questions", async (
                int id, ReadingQuestionInput input,
                HttpContext ctx, AppDbContext db, IReadingService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                if (string.IsNullOrWhiteSpace(input.Prompt))
                    return Results.BadRequest(new { error = "prompt is required." });
                if (string.IsNullOrWhiteSpace(input.CorrectAnswer))
                    return Results.BadRequest(new { error = "correctAnswer is required." });

                try
                {
                    return MapWrite(await service.AddQuestionAsync(user.Id, id, input, ct), Results.Ok);
                }
                catch (ArgumentException ex)
                {
                    // An unsupported type is the caller's mistake, not a server fault:
                    // scoring only handles the formats it can decide deterministically.
                    return Results.BadRequest(new
                    {
                        error = ex.Message,
                        supported = ReadingQuestionTypes.All,
                    });
                }
            });

            app.MapPut("/ielts/reading/questions/{id:int}", async (
                int id, ReadingQuestionInput input,
                HttpContext ctx, AppDbContext db, IReadingService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                try
                {
                    return MapWrite(await service.UpdateQuestionAsync(user.Id, id, input, ct), Results.Ok);
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message, supported = ReadingQuestionTypes.All });
                }
            });

            app.MapDelete("/ielts/reading/questions/{id:int}", async (
                int id, HttpContext ctx, AppDbContext db, IReadingService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return MapWrite(await service.DeleteQuestionAsync(user.Id, id, ct), _ => Results.NoContent());
            });

            // ---- attempts ----

            // Returns the questions WITHOUT their answer keys.
            app.MapPost("/ielts/reading/tests/{id:int}/attempts", async (
                int id, HttpContext ctx, AppDbContext db, IReadingService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var dto = await service.StartAttemptAsync(user.Id, id, ct);
                return dto is null ? Results.NotFound() : Results.Ok(dto);
            });

            app.MapPost("/ielts/reading/attempts/{id:int}/submit", async (
                int id, SubmitAttemptRequest body,
                HttpContext ctx, AppDbContext db, IReadingService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var dto = await service.SubmitAttemptAsync(
                    user.Id, id,
                    body?.Answers ?? new List<ReadingAnswerInput>(),
                    body?.DurationSeconds, ct);

                return dto is null ? Results.NotFound() : Results.Ok(dto);
            });

            app.MapGet("/ielts/reading/attempts", async (
                HttpContext ctx, AppDbContext db, IReadingService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return Results.Ok(await service.ListAttemptsAsync(user.Id, ct));
            });

            app.MapGet("/ielts/reading/attempts/{id:int}", async (
                int id, HttpContext ctx, AppDbContext db, IReadingService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var dto = await service.GetAttemptAsync(user.Id, id, ct);
                return dto is null ? Results.NotFound() : Results.Ok(dto);
            });

            // The formats the author may pick from — sourced from the same
            // constant scoring validates against, so the picker cannot drift.
            app.MapGet("/ielts/reading/question-types", async (
                HttpContext ctx, AppDbContext db) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return Results.Ok(ReadingQuestionTypes.All);
            });
        }
    }
}
