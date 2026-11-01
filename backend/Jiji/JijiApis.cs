// Jiji — HTTP surface, /api/jiji (docs/JIJI_PLAN.md, Endpoints).
//
//   GET    /api/jiji/status                          what is configured (storage, model, voice, speech, YouTube)
//   POST   /api/jiji/ideas                           5 angles from a content type, subject and your notes (nothing saved)
//   GET    /api/jiji/contents?status=                the queue, newest change first
//   POST   /api/jiji/contents                        a new item, once an angle is picked
//   GET    /api/jiji/contents/{id}                   one item, its script, its jobs and the approval check
//   PUT    /api/jiji/contents/{id}                   subject, angle, notes (idea or draft only)
//   DELETE /api/jiji/contents/{id}                   an idea or draft you no longer want
//   POST   /api/jiji/contents/{id}/script/generate   the model writes the script JSON
//   PUT    /api/jiji/contents/{id}/script            save your edits
//   POST   /api/jiji/contents/{id}/approve-script    originalAngle and myTake required; queues TTS
//   GET    /api/jiji/contents/{id}/audio             narration.wav of the current revision (range requests)
//   GET    /api/jiji/contents/{id}/timings           every spoken part's start and end in it
//   POST   /api/jiji/jobs/{jobId}/retry              a Failed job back to Pending; refused for UnknownOutcome
//
// Every write names expectedUpdatedAt and answers 409 when it is stale. 422 carries
// { error, issues[] } for a script that is malformed or breaks a rule; nothing is saved.
// The `jiji` tab gate is TabAccessMiddleware's; a row belongs to its OwnerId, and
// someone else's id reads as 404.

using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Jiji.Dtos;
using RabbitIC.Api.Jiji.Services;

namespace RabbitIC.Api.Jiji;

public static class JijiApis
{
    public static IEndpointRouteBuilder MapJijiApis(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/jiji");

        g.MapGet("/status", async (HttpContext ctx, AppDbContext db, JijiContentService svc, CancellationToken ct) =>
            await Authorization.ResolveUser(ctx, db) is { } user
                ? Results.Ok(await svc.StatusAsync(user.Id, ct))
                : Results.Unauthorized());

        g.MapPost("/ideas", async (JijiIdeaRequest req, HttpContext ctx, AppDbContext db, IdeaGenerator ideas, CancellationToken ct) =>
            await Authorization.ResolveUser(ctx, db) is { } user
                ? Map(await ideas.GenerateAsync(user, req, ct))
                : Results.Unauthorized());

        g.MapGet("/contents", async (string? status, HttpContext ctx, AppDbContext db, JijiContentService svc, CancellationToken ct) =>
            await Authorization.ResolveUser(ctx, db) is { } user
                ? Results.Ok(await svc.ListAsync(user.Id, status, ct))
                : Results.Unauthorized());

        g.MapPost("/contents", async (JijiCreateContentRequest req, HttpContext ctx, AppDbContext db, JijiContentService svc, CancellationToken ct) =>
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) return Results.Unauthorized();
            var r = await svc.CreateAsync(user.Id, req, ct);
            return r.Kind == JijiResultKind.Ok ? Results.Json(r.Value, statusCode: StatusCodes.Status201Created) : Map(r);
        });

        g.MapGet("/contents/{id:int}", async (int id, HttpContext ctx, AppDbContext db, JijiContentService svc, CancellationToken ct) =>
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) return Results.Unauthorized();
            return await svc.GetAsync(user.Id, id, ct) is { } dto
                ? Results.Ok(dto)
                : Results.NotFound(new { error = "No such item." });
        });

        g.MapPut("/contents/{id:int}", async (int id, JijiUpdateContentRequest req, HttpContext ctx, AppDbContext db, JijiContentService svc, CancellationToken ct) =>
            await Authorization.ResolveUser(ctx, db) is { } user
                ? Map(await svc.UpdateAsync(user.Id, id, req, ct))
                : Results.Unauthorized());

        g.MapDelete("/contents/{id:int}", async (int id, HttpContext ctx, AppDbContext db, JijiContentService svc, CancellationToken ct) =>
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) return Results.Unauthorized();
            var r = await svc.DeleteAsync(user.Id, id, ct);
            return r.Kind == JijiResultKind.Ok ? Results.NoContent() : Map(r);
        });

        g.MapPost("/contents/{id:int}/script/generate", async (int id, JijiGenerateScriptRequest req, HttpContext ctx, AppDbContext db, JijiContentService svc, CancellationToken ct) =>
            await Authorization.ResolveUser(ctx, db) is { } user
                ? Map(await svc.GenerateScriptAsync(user, id, req, ct))
                : Results.Unauthorized());

        g.MapPut("/contents/{id:int}/script", async (int id, JijiSaveScriptRequest req, HttpContext ctx, AppDbContext db, JijiContentService svc, CancellationToken ct) =>
            await Authorization.ResolveUser(ctx, db) is { } user
                ? Map(await svc.SaveScriptAsync(user.Id, id, req, ct))
                : Results.Unauthorized());

        g.MapPost("/contents/{id:int}/approve-script", async (int id, JijiApproveScriptRequest req, HttpContext ctx, AppDbContext db, JijiContentService svc, CancellationToken ct) =>
            await Authorization.ResolveUser(ctx, db) is { } user
                ? Map(await svc.ApproveScriptAsync(user.Id, id, req, ct))
                : Results.Unauthorized());

        g.MapGet("/contents/{id:int}/audio", async (int id, HttpContext ctx, AppDbContext db, JijiContentService svc, CancellationToken ct) =>
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) return Results.Unauthorized();
            return await svc.NarrationPathAsync(user.Id, id, ct) is { } path
                ? Results.File(path, "audio/wav", enableRangeProcessing: true)
                : Results.NotFound(new { error = "No narration for this revision yet." });
        });

        g.MapGet("/contents/{id:int}/timings", async (int id, HttpContext ctx, AppDbContext db, JijiContentService svc, CancellationToken ct) =>
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) return Results.Unauthorized();
            return await svc.TimingsAsync(user.Id, id, ct) is { } t
                ? Results.Ok(t)
                : Results.NotFound(new { error = "No narration for this revision yet." });
        });

        g.MapPost("/jobs/{jobId:int}/retry", async (int jobId, HttpContext ctx, AppDbContext db, JijiContentService svc, CancellationToken ct) =>
            await Authorization.ResolveUser(ctx, db) is { } user
                ? Map(await svc.RetryJobAsync(user.Id, jobId, ct))
                : Results.Unauthorized());

        return app;
    }

    private static IResult Map<T>(JijiResult<T> r) => r.Kind switch
    {
        JijiResultKind.Ok => Results.Ok(r.Value),
        JijiResultKind.NotFound => Results.NotFound(new { error = r.Error }),
        JijiResultKind.Conflict => Results.Conflict(new { error = r.Error }),
        JijiResultKind.Forbidden => Results.Json(new { error = r.Error }, statusCode: StatusCodes.Status403Forbidden),
        JijiResultKind.Upstream => Results.Json(new { error = r.Error, issues = r.Issues ?? [] }, statusCode: StatusCodes.Status502BadGateway),
        _ => Results.UnprocessableEntity(new { error = r.Error, issues = r.Issues ?? [] }),
    };
}
