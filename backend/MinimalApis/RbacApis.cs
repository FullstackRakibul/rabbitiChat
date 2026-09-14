// RBAC — HTTP surface.
//
// Two groups with different audiences:
//   /api/me/*          — the caller asking about themselves. Any session.
//   /api/admin/rbac/*  — administering other users. SystemAdmin only.
//
// No endpoint here decides a rule: each resolves the session, hands the caller
// plus the input to RbacService, and maps the returned outcome to a status code.
// A non-admin therefore gets 403 from every admin route by the same code path,
// with no route able to forget the check.

using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.DTOs.Rbac;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.MinimalApis
{
    public static class RbacApis
    {
        public static IEndpointRouteBuilder MapRbacApis(this IEndpointRouteBuilder app)
        {
            MapMyTabs(app);
            MapAdmin(app);
            return app;
        }

        /// <summary>
        /// What the shell calls on login to learn which tabs to render. Not an
        /// admin route: it only ever describes the caller.
        /// </summary>
        private static void MapMyTabs(IEndpointRouteBuilder app)
        {
            app.MapGet("/me/tabs", async (
                HttpContext ctx, AppDbContext db, RbacService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return Results.Ok(await svc.GetMyTabsAsync(user, ct));
            })
            .WithSummary("The caller's own role and effective tabs.");
        }

        private static void MapAdmin(IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/admin/rbac");

            // The tab catalogue for the admin checklist. Admin-only like the rest of
            // the group — the shape of the permission system is not public.
            group.MapGet("/tabs", async (
                HttpContext ctx, AppDbContext db, RbacService svc) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                if (!Models.UserRoles.IsSystemAdmin(user))
                    return Forbid("SystemAdmin role required.");

                return Results.Ok(svc.ListTabs());
            })
            .WithSummary("Known tab keys, for the permission checklist.");

            group.MapGet("/users", async (
                HttpContext ctx, AppDbContext db, RbacService svc,
                string? search, int? page, int? pageSize, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var result = await svc.SearchUsersAsync(user, search, page ?? 1, pageSize ?? 20, ct);
                return Map(result);
            })
            .WithSummary("Search users by username (paged), with role and tab count.");

            group.MapGet("/users/{id:int}", async (
                int id, HttpContext ctx, AppDbContext db, RbacService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return Map(await svc.GetUserAsync(user, id, ct));
            })
            .WithSummary("One user: role, effective tabs, and explicit grants.");

            group.MapPut("/users/{id:int}/role", async (
                int id, SetRoleInput input, HttpContext ctx, AppDbContext db,
                RbacService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return Map(await svc.SetRoleAsync(user, id, input, ct));
            })
            .WithSummary("Set a user's role. Refuses self-edits and demoting the last admin.");

            group.MapPut("/users/{id:int}/tabs", async (
                int id, SetTabsInput input, HttpContext ctx, AppDbContext db,
                RbacService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return Map(await svc.SetTabsAsync(user, id, input, ct));
            })
            .WithSummary("Replace a user's explicit tab grants.");
        }

        /// <summary>
        /// The one place an RbacOutcome becomes a status code, so every route in
        /// the group refuses in exactly the same way.
        /// </summary>
        private static IResult Map<T>(RbacResult<T> result) => result.Outcome switch
        {
            RbacOutcome.Ok => Results.Ok(result.Value),
            RbacOutcome.NotFound => Results.NotFound(new { error = result.Error }),
            RbacOutcome.Forbidden => Forbid(result.Error),
            RbacOutcome.Conflict => Results.Conflict(new { error = result.Error }),
            RbacOutcome.Invalid => Results.BadRequest(new { error = result.Error }),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };

        /// <summary>
        /// 403 with a body. Results.Forbid() authenticates against a scheme this
        /// app has none of, so the status is written directly.
        /// </summary>
        private static IResult Forbid(string? error) =>
            Results.Json(new { error = error ?? "Forbidden." }, statusCode: StatusCodes.Status403Forbidden);
    }
}
