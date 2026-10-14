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

using Microsoft.EntityFrameworkCore;
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

            // ---- Model policy (§3.3) ----
            // Which models each role may run. Admin-only, like every route here.

            group.MapGet("/model-policies", async (
                HttpContext ctx, AppDbContext db, Services.ChatService chat,
                CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                if (!Models.UserRoles.IsSystemAdmin(user)) return Forbid("SystemAdmin role required.");

                var rows = await db.RoleModelPolicies.AsNoTracking().ToListAsync(ct);
                var byRole = rows.ToDictionary(r => r.Role, StringComparer.OrdinalIgnoreCase);

                // The installed LOCAL models are the only valid choices: a
                // restricted role is confined to local anyway (§3.1).
                var installed = (await chat.ListAllModelsAsync(ct))
                    .Where(m => string.Equals(m.Source, "local", StringComparison.OrdinalIgnoreCase))
                    .Select(m => m.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var policies = Models.UserRoles.All.Select(role =>
                {
                    byRole.TryGetValue(role, out var row);
                    var allowed = Rbac.ModelPolicy.Parse(row?.AllowedModels).ToList();
                    return new ModelPolicyDto(
                        role, allowed, row?.DefaultModel ?? "",
                        allowed.Count == 1,
                        role != Models.UserRoles.SystemAdmin,
                        row?.UpdatedAtUtc ?? default);
                }).ToList();

                return Results.Ok(new ModelPolicyPageDto(policies, installed));
            })
            .WithSummary("Model policy for every role, and the installed models to choose from.");

            group.MapPut("/model-policies/{role}", async (
                string role, SetModelPolicyInput input,
                HttpContext ctx, AppDbContext db, Services.ChatService chat,
                Rbac.IModelPolicy modelPolicy, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                if (!Models.UserRoles.IsSystemAdmin(user)) return Forbid("SystemAdmin role required.");

                // Normalize() would fold an unknown name to General and silently
                // rewrite the wrong role, so match exactly instead.
                var canonical = Models.UserRoles.All.FirstOrDefault(
                    r => string.Equals(r, role?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (canonical is null)
                    return Results.BadRequest(new { error = $"'{role}' is not a known role." });

                var allowed = (input.AllowedModels ?? new List<string>())
                    .Select(m => m?.Trim() ?? "")
                    .Where(m => m.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // An admin who restricted their own role could not reach the screen
                // that would undo it.
                if (canonical == Models.UserRoles.SystemAdmin && allowed.Count > 0)
                    return Results.BadRequest(new { error = "SystemAdmin cannot be restricted to a model list." });

                var installed = (await chat.ListAllModelsAsync(ct))
                    .Where(m => string.Equals(m.Source, "local", StringComparison.OrdinalIgnoreCase))
                    .Select(m => m.Name)
                    .ToList();

                var unknown = allowed
                    .Where(a => !installed.Any(i => Rbac.ModelPolicy.SameModel(i, a)))
                    .ToList();
                if (unknown.Count > 0)
                    return Results.BadRequest(new { error = $"Not installed: {string.Join(", ", unknown)}." });

                var def = input.DefaultModel?.Trim() ?? "";
                if (allowed.Count > 0 && def.Length > 0
                    && !allowed.Any(a => Rbac.ModelPolicy.SameModel(a, def)))
                {
                    return Results.BadRequest(new { error = $"The default '{def}' is not in the allowed list." });
                }
                if (allowed.Count == 0) def = "";

                var row = await db.RoleModelPolicies.FirstOrDefaultAsync(r => r.Role == canonical, ct);
                if (row is null)
                {
                    row = new Models.RoleModelPolicy { Role = canonical };
                    db.RoleModelPolicies.Add(row);
                }

                row.AllowedModels = string.Join(",", allowed);
                row.DefaultModel = def;
                row.UpdatedAtUtc = DateTime.UtcNow;
                row.UpdatedByUserId = user.Id;

                await db.SaveChangesAsync(ct);

                // The next request reads the new rule, with no restart.
                modelPolicy.Invalidate();

                return Results.Ok(new ModelPolicyDto(
                    canonical, allowed, def, allowed.Count == 1,
                    canonical != Models.UserRoles.SystemAdmin, row.UpdatedAtUtc));
            })
            .WithSummary("Replace one role's model policy.");

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

            group.MapGet("/roles", async (
                HttpContext ctx, AppDbContext db, RbacService svc) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                if (!Models.UserRoles.IsSystemAdmin(user))
                    return Forbid("SystemAdmin role required.");

                return Results.Ok(svc.ListRoles());
            })
            .WithSummary("Every role with its label and default tabs, for the role picker.");

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
