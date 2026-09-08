using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.MinimalApis
{
    /// <summary>Body of PUT /context-sources/reorder.</summary>
    public record ReorderRequest(List<int>? Ids);

    /// <summary>
    /// HTTP surface over <see cref="IContextSourceService"/>. It holds no rules of
    /// its own — every decision (ordering, ownership, defaults) belongs to the
    /// service, so the workspace tab gets identical behaviour through the same
    /// endpoints with kind="buyer".
    /// </summary>
    public static class ContextSourceApis
    {
        public static IEndpointRouteBuilder MapContextSourceApis(this IEndpointRouteBuilder app)
        {
            // GET /api/context-sources?kind=source|buyer — omit kind for everything.
            app.MapGet("/context-sources", async (
                string? kind, HttpContext ctx, AppDbContext db,
                IContextSourceService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return Results.Ok(await service.ListAsync(user.Id, kind, ct));
            });

            app.MapGet("/context-sources/{id:int}", async (
                int id, HttpContext ctx, AppDbContext db,
                IContextSourceService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                // Absent and foreign are the same answer on purpose — a 403 here
                // would confirm that someone else's id exists.
                var dto = await service.GetAsync(user.Id, id, ct);
                return dto is null ? Results.NotFound() : Results.Ok(dto);
            });

            app.MapPost("/context-sources", async (
                ContextSourceInput input, HttpContext ctx, AppDbContext db,
                IContextSourceService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                if (string.IsNullOrWhiteSpace(input.Name))
                    return Results.BadRequest(new { error = "name is required." });

                var dto = await service.CreateAsync(user.Id, input, ct);
                return Results.Created($"/api/context-sources/{dto.Id}", dto);
            });

            app.MapPut("/context-sources/{id:int}", async (
                int id, ContextSourceInput input, HttpContext ctx, AppDbContext db,
                IContextSourceService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var dto = await service.UpdateAsync(user.Id, id, input, ct);
                return dto is null ? Results.NotFound() : Results.Ok(dto);
            });

            // PUT /api/context-sources/reorder — the full id list in its new order.
            // Declared before the {id:int} route would ever be considered for
            // "reorder"; the int constraint already keeps them apart.
            app.MapPut("/context-sources/reorder", async (
                ReorderRequest req, HttpContext ctx, AppDbContext db,
                IContextSourceService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                await service.ReorderAsync(user.Id, req.Ids ?? new List<int>(), ct);
                return Results.Ok(await service.ListAsync(user.Id, null, ct));
            });

            app.MapDelete("/context-sources/{id:int}", async (
                int id, HttpContext ctx, AppDbContext db,
                IContextSourceService service, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return await service.DeleteAsync(user.Id, id, ct)
                    ? Results.NoContent()
                    : Results.NotFound();
            });

            return app;
        }
    }
}
