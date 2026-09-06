// todorabbit — a creation of Mostisko, the AI.
//
// HTTP surface for the board module, mounted at /api/todorabbit. Every endpoint
// requires a session and is scoped to the caller. A row that belongs to someone
// else answers 404, never 403, so ownership cannot be probed by id.

using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.DTOs.TodoRabbit;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.MinimalApis
{
    public static class TodoRabbitApis
    {
        public static IEndpointRouteBuilder MapTodoRabbitApis(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/todorabbit");

            // ---- Boards ----

            group.MapGet("/boards", async (
                HttpContext ctx, AppDbContext db, TodoRabbitService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return Results.Ok(await svc.ListBoardsAsync(user.Id, ct));
            });

            // Seeds three default columns so a new board is usable immediately.
            group.MapPost("/boards", async (
                BoardInput input, HttpContext ctx, AppDbContext db,
                TodoRabbitService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var board = await svc.CreateBoardAsync(user.Id, input, ct);
                return Results.Created($"/api/todorabbit/boards/{board.Id}", board);
            });

            group.MapGet("/boards/{id:int}", async (
                int id, HttpContext ctx, AppDbContext db, TodoRabbitService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var board = await svc.GetBoardAsync(id, user.Id, ct);
                return board is null ? Results.NotFound() : Results.Ok(board);
            });

            group.MapPut("/boards/{id:int}", async (
                int id, BoardInput input, HttpContext ctx, AppDbContext db,
                TodoRabbitService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var board = await svc.RenameBoardAsync(id, user.Id, input, ct);
                return board is null ? Results.NotFound() : Results.Ok(board);
            });

            group.MapDelete("/boards/{id:int}", async (
                int id, HttpContext ctx, AppDbContext db, TodoRabbitService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return await svc.DeleteBoardAsync(id, user.Id, ct)
                    ? Results.NoContent()
                    : Results.NotFound();
            });

            // ---- Columns ----

            group.MapPost("/boards/{id:int}/columns", async (
                int id, ColumnInput input, HttpContext ctx, AppDbContext db,
                TodoRabbitService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var column = await svc.AddColumnAsync(id, user.Id, input, ct);
                return column is null
                    ? Results.NotFound()
                    : Results.Created($"/api/todorabbit/columns/{column.Id}", column);
            });

            // Reorder is declared before /columns/{id} so "reorder" is never
            // swallowed as an id segment.
            group.MapPut("/columns/reorder", async (
                ReorderInput input, HttpContext ctx, AppDbContext db,
                TodoRabbitService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var columns = await svc.ReorderColumnsAsync(user.Id, input, ct);
                return columns is null ? Results.NotFound() : Results.Ok(columns);
            });

            group.MapPut("/columns/{id:int}", async (
                int id, ColumnInput input, HttpContext ctx, AppDbContext db,
                TodoRabbitService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var column = await svc.UpdateColumnAsync(id, user.Id, input, ct);
                return column is null ? Results.NotFound() : Results.Ok(column);
            });

            group.MapDelete("/columns/{id:int}", async (
                int id, HttpContext ctx, AppDbContext db, TodoRabbitService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return await svc.DeleteColumnAsync(id, user.Id, ct)
                    ? Results.NoContent()
                    : Results.NotFound();
            });

            // ---- Cards ----

            group.MapPost("/columns/{id:int}/cards", async (
                int id, CardInput input, HttpContext ctx, AppDbContext db,
                TodoRabbitService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var card = await svc.AddCardAsync(id, user.Id, input, ct);
                return card is null
                    ? Results.NotFound()
                    : Results.Created($"/api/todorabbit/cards/{card.Id}", card);
            });

            group.MapPut("/cards/{id:int}", async (
                int id, CardInput input, HttpContext ctx, AppDbContext db,
                TodoRabbitService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var card = await svc.UpdateCardAsync(id, user.Id, input, ct);
                return card is null ? Results.NotFound() : Results.Ok(card);
            });

            group.MapDelete("/cards/{id:int}", async (
                int id, HttpContext ctx, AppDbContext db, TodoRabbitService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return await svc.DeleteCardAsync(id, user.Id, ct)
                    ? Results.NoContent()
                    : Results.NotFound();
            });

            group.MapPut("/cards/{id:int}/move", async (
                int id, MoveCardInput input, HttpContext ctx, AppDbContext db,
                TodoRabbitService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var card = await svc.MoveCardAsync(id, user.Id, input, ct);
                return card is null ? Results.NotFound() : Results.Ok(card);
            });

            return app;
        }
    }
}
