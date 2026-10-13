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

            // ---- Models by role ----
            // The policy editor behind Access. Reading and writing are both
            // SystemAdmin only; the rule itself lives in Rbac/ModelPolicy.cs and
            // is never restated here.
            group.MapGet("/model-policies", async (
                HttpContext ctx, AppDbContext db, ChatService chat,
                Rbac.ModelPolicy modelPolicy, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                if (!Models.UserRoles.IsSystemAdmin(user))
                    return Forbid("SystemAdmin role required.");

                var rows = await modelPolicy.RowsAsync(ct);

                // Only local models can be allowed (§3.1), so only those are
                // offered as choices — the editor cannot propose a cloud model.
                var installed = (await chat.ListAllModelsAsync(ct))
                    .Where(m => m.Source.Equals(Rbac.ModelPolicy.LocalSource, StringComparison.OrdinalIgnoreCase))
                    .Select(m => m.Name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var policies = Models.UserRoles.All.Select(role =>
                {
                    rows.TryGetValue(role, out var row);
                    return new RoleModelPolicyDto(
                        Role: role,
                        Label: Models.UserRoles.LabelFor(role),
                        AllowedModels: Rbac.ModelPolicy.Split(row?.AllowedModels),
                        DefaultModel: row?.DefaultModel ?? "",
                        // SystemAdmin can never be restricted, so the editor shows
                        // its row read-only with the reason rather than a control
                        // that would always be refused.
                        CanRestrict: role != Models.UserRoles.SystemAdmin,
                        UpdatedAtUtc: row?.UpdatedAtUtc);
                }).ToList();

                return Results.Ok(new { policies, installedModels = installed });
            })
            .WithSummary("Which models each role may run, plus the installed local models to choose from.");

            group.MapPut("/model-policies/{role}", async (
                string role, RoleModelPolicyInput input, HttpContext ctx, AppDbContext db,
                ChatService chat, Rbac.ModelPolicy modelPolicy, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                if (!Models.UserRoles.IsSystemAdmin(user))
                    return Forbid("SystemAdmin role required.");

                // An unknown role would otherwise create a row nothing reads.
                if (!Models.UserRoles.IsKnown(role))
                    return Results.BadRequest(new { error = $"'{role}' is not a known role." });

                var canonical = Models.UserRoles.Normalize(role);
                var allowed = (input.AllowedModels ?? new List<string>())
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Select(m => m.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var defaultModel = (input.DefaultModel ?? "").Trim();

                // An admin who locked their own role out of every model could not
                // undo it through a UI that needs a model to work.
                if (canonical == Models.UserRoles.SystemAdmin && allowed.Count > 0)
                    return Results.BadRequest(new
                    {
                        error = "SystemAdmin cannot be restricted — an administrator must always be able to "
                              + "run any model, or a mistake here could not be undone.",
                    });

                if (allowed.Count > 0)
                {
                    // Only installed local models: allowing a name nothing serves
                    // would refuse the role at run time for no visible reason.
                    var installed = (await chat.ListAllModelsAsync(ct))
                        .Where(m => m.Source.Equals(Rbac.ModelPolicy.LocalSource, StringComparison.OrdinalIgnoreCase))
                        .Select(m => m.Name)
                        .ToList();

                    var unknown = allowed
                        .Where(a => !installed.Any(i => Rbac.ModelPolicy.SameModel(i, a)))
                        .ToList();
                    if (unknown.Count > 0)
                        return Results.BadRequest(new
                        {
                            error = $"Not installed locally: {string.Join(", ", unknown)}.",
                        });

                    if (string.IsNullOrWhiteSpace(defaultModel))
                        return Results.BadRequest(new { error = "A default model is required when the list is not empty." });

                    if (!allowed.Any(a => Rbac.ModelPolicy.SameModel(a, defaultModel)))
                        return Results.BadRequest(new
                        {
                            error = $"The default '{defaultModel}' is not in the allowed list.",
                        });
                }
                else
                {
                    // No restriction means no default either: the caller's own
                    // fallback decides, as it always has.
                    defaultModel = "";
                }

                var row = await db.RoleModelPolicies.FirstOrDefaultAsync(p => p.Role == canonical, ct);
                if (row is null)
                {
                    row = new Models.RoleModelPolicy { Role = canonical };
                    db.RoleModelPolicies.Add(row);
                }

                row.AllowedModels = string.Join(",", allowed);
                row.DefaultModel = defaultModel;
                row.UpdatedAtUtc = DateTime.UtcNow;
                row.UpdatedByUserId = user.Id;
                await db.SaveChangesAsync(ct);

                // So the change applies to the very next request, with no restart.
                modelPolicy.Invalidate();

                return Results.Ok(new RoleModelPolicyDto(
                    canonical, Models.UserRoles.LabelFor(canonical),
                    allowed, defaultModel,
                    canonical != Models.UserRoles.SystemAdmin, row.UpdatedAtUtc));
            })
            .WithSummary("Save one role's allowed models. SystemAdmin cannot be restricted.");

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
