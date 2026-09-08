using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.MinimalApis
{
    /// <summary>Body of PUT /search/history/{id}.</summary>
    public record RenameHistoryRequest(string? Title);

    /// <summary>
    /// HTTP surface over <see cref="ISearchHistoryService"/>.
    ///
    /// There is deliberately no "get results by id" route: results are never
    /// stored, so reuse means re-running the query through the existing
    /// /api/search proxy. A route that appeared to return saved results would be
    /// lying about what this feature keeps.
    /// </summary>
    public static class SearchHistoryApis
    {
        public static IEndpointRouteBuilder MapSearchHistoryApis(this IEndpointRouteBuilder app)
        {
            // GET /api/search/history — active rows, newest run first.
            // ?deleted=true lists the soft-deleted ones instead, for the restore view.
            app.MapGet("/search/history", async (
                bool? deleted, HttpContext ctx, AppDbContext db,
                ISearchHistoryService history, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return Results.Ok(deleted == true
                    ? await history.ListDeletedAsync(user.Id, ct)
                    : await history.ListAsync(user.Id, ct));
            });

            // PUT /api/search/history/{id} — rename. An empty title clears it, so
            // the row falls back to displaying the query.
            app.MapPut("/search/history/{id:int}", async (
                int id, RenameHistoryRequest req, HttpContext ctx, AppDbContext db,
                ISearchHistoryService history, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                // Absent and foreign are the same answer on purpose — a 403 would
                // confirm that someone else's id exists.
                var dto = await history.RenameAsync(user.Id, id, req.Title, ct);
                return dto is null ? Results.NotFound() : Results.Ok(dto);
            });

            // DELETE /api/search/history/{id} — soft delete. The row stays so the
            // client's Undo is a real restore rather than a re-create.
            app.MapDelete("/search/history/{id:int}", async (
                int id, HttpContext ctx, AppDbContext db,
                ISearchHistoryService history, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var dto = await history.SoftDeleteAsync(user.Id, id, ct);
                return dto is null ? Results.NotFound() : Results.Ok(dto);
            });

            app.MapPost("/search/history/{id:int}/restore", async (
                int id, HttpContext ctx, AppDbContext db,
                ISearchHistoryService history, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var dto = await history.RestoreAsync(user.Id, id, ct);
                return dto is null ? Results.NotFound() : Results.Ok(dto);
            });

            return app;
        }
    }
}
