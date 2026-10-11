// Oddity — the Ground Control API under /api/oddity.
//
// Session-token routes behind the "oddity" tab, which TabAccessMiddleware
// enforces for the whole prefix. Keys and requests are owner-scoped: someone
// else's id reads as absent (404, never 403). The settings row is install-wide.

using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.DTOs.Oddity;
using RabbitIC.Api.Oddity;

namespace RabbitIC.Api.MinimalApis
{
    public static class OddityAdminApis
    {
        public static IEndpointRouteBuilder MapOddityAdminApis(this IEndpointRouteBuilder app)
        {
            var g = app.MapGroup("/oddity");

            // ---- Settings ----

            g.MapGet("/settings", async (HttpContext ctx, AppDbContext db, OdditySettingsService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return Results.Ok(OdditySettingsService.ToDto(await svc.GetAsync(ct)));
            })
            .WithSummary("The gateway's install-wide settings.")
            .Produces<OdditySettingsDto>();

            g.MapPut("/settings", async (OdditySettingsInput input, HttpContext ctx, AppDbContext db,
                OdditySettingsService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var (saved, errors) = await svc.SaveAsync(input, user.Id, ct);

                // "error" carries every message too: it is the one field http.js hands
                // the UI; "errors" keeps them keyed by field.
                return saved is null
                    ? Results.BadRequest(new { error = string.Join(" ", errors.Values), errors })
                    : Results.Ok(saved);
            })
            .WithSummary("Replace the settings. Applies to the next gateway call, no restart.")
            .Produces<OdditySettingsDto>()
            .Produces(StatusCodes.Status400BadRequest);

            // ---- Keys ----

            g.MapGet("/keys", async (HttpContext ctx, AppDbContext db, OddityKeyService keys, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return Results.Ok(await keys.ListAsync(user.Id, ct));
            })
            .WithSummary("Your gateway keys, with last use and 24 h count. Never the key itself.")
            .Produces<List<OddityKeyDto>>();

            g.MapPost("/keys", async (OddityKeyInput input, HttpContext ctx, AppDbContext db,
                OddityKeyService keys, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var (created, error) = await keys.CreateAsync(user.Id, input, ct);
                return created is null ? Results.BadRequest(new { error }) : Results.Ok(created);
            })
            .WithSummary("Create a key. The response is the only time the key is shown.")
            .Produces<OddityKeyCreatedDto>()
            .Produces(StatusCodes.Status400BadRequest);

            g.MapPut("/keys/{id:int}", async (int id, OddityKeyInput input, HttpContext ctx, AppDbContext db,
                OddityKeyService keys, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var (row, error) = await keys.UpdateAsync(user.Id, id, input, ct);
                if (row is null) return Results.NotFound(new { error = "No such key." });
                return error is null ? Results.Ok(OddityKeyService.ToDto(row)) : Results.BadRequest(new { error });
            })
            .WithSummary("Change a key's name, note, rate, enabled state or expiry.")
            .Produces<OddityKeyDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

            g.MapDelete("/keys/{id:int}", async (int id, HttpContext ctx, AppDbContext db,
                OddityKeyService keys, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return await keys.DeleteAsync(user.Id, id, ct)
                    ? Results.NoContent()
                    : Results.NotFound(new { error = "No such key." });
            })
            .WithSummary("Revoke a key. Its request history stays.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

            // ---- Requests ----

            g.MapGet("/requests", async ([AsParameters] OddityRequestFilter filter, HttpContext ctx, AppDbContext db,
                OddityRecorder recorder, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                return Results.Ok(await recorder.ListAsync(user.Id, filter, ct));
            })
            .WithSummary("Your gateway calls, newest first. Page with ?before=<nextBefore>.")
            .Produces<OddityRequestPageDto>();

            g.MapGet("/requests/{id:long}", async (long id, HttpContext ctx, AppDbContext db,
                OddityRecorder recorder, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                var row = await recorder.GetAsync(user.Id, id, ct);
                return row is null ? Results.NotFound(new { error = "No such request." }) : Results.Ok(row);
            })
            .WithSummary("One gateway call in full, with its previews.")
            .Produces<OddityRequestDetailDto>()
            .Produces(StatusCodes.Status404NotFound);

            return app;
        }
    }
}
