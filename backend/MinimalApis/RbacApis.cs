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
            MapAccess(app);
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

            app.MapGet("/me/access", async (
                HttpContext ctx, AppDbContext db, RbacService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return Results.Ok(await svc.GetMyAccessAsync(user, ct));
            })
            .WithSummary("The caller's tabs (with labels and menu groups), sections, actions and model policy.");
        }

        /// <summary>
        /// Access (docs/RBAC_ROLES_PROMPT.md §3.4): the catalog and the role presets.
        /// Access managers only — SystemAdmin, or a role given action:access.manage.
        /// </summary>
        private static void MapAccess(IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/access");

            group.MapGet("/catalog", async (
                HttpContext ctx, AppDbContext db, RbacService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                if (!await svc.CanManageAsync(user, ct)) return Forbid(ManagersOnly);

                return Results.Ok(RbacService.Catalog());
            })
            .WithSummary("Every tab, section and action: labels (English, Bengali), group and parent.");

            group.MapGet("/roles", async (
                HttpContext ctx, AppDbContext db, RbacService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return Map(await svc.GetRolePresetsAsync(user, ct));
            })
            .WithSummary("Every role with its value for every key, and how many users hold it.");

            group.MapPut("/roles/{role}", async (
                string role, SaveRolePresetInput input, HttpContext ctx, AppDbContext db,
                RbacService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return Map(await svc.SaveRolePresetAsync(user, role, input, ct));
            })
            .WithSummary("Save a role's keys (changed keys only), with an optional note. 400 names a broken invariant.");
        }

        private static void MapAdmin(IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/admin/rbac");

            // The tab catalogue for the admin checklist. Admin-only like the rest of
            // the group — the shape of the permission system is not public.
            group.MapGet("/tabs", async (
                HttpContext ctx, AppDbContext db, RbacService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                if (!await svc.CanManageAsync(user, ct))
                    return Forbid(ManagersOnly);

                return Results.Ok(svc.ListTabs());
            })
            .WithSummary("Known tab keys, for the permission checklist.");

            group.MapGet("/roles", async (
                HttpContext ctx, AppDbContext db, RbacService svc, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                if (!await svc.CanManageAsync(user, ct))
                    return Forbid(ManagersOnly);

                return Results.Ok(await svc.ListRolesAsync(ct));
            })
            .WithSummary("Every role with its label and default tabs, for the role picker.");

            // ---- Models by role ----
            // The policy editor behind Access. Reading and writing are both
            // SystemAdmin only; the rule itself lives in Rbac/ModelPolicy.cs and
            // is never restated here.
            group.MapGet("/model-policies", async (
                HttpContext ctx, AppDbContext db, ChatService chat, RbacService svc,
                Rbac.ModelPolicy modelPolicy, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                if (!await svc.CanManageAsync(user, ct))
                    return Forbid(ManagersOnly);

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
                    var allowedModels = Rbac.ModelPolicy.Split(row?.AllowedModels);

                    // A model can be uninstalled from the Mac long after a policy
                    // named it. Say which ones those are rather than listing them
                    // as though they still worked: an editor that cannot tell the
                    // difference shows a tick for a model that no longer exists,
                    // and then posts it straight back on the next save.
                    var stale = allowedModels
                        .Where(a => !installed.Any(i => Rbac.ModelPolicy.SameModel(i, a)))
                        .ToList();

                    return new RoleModelPolicyDto(
                        Role: role,
                        Label: Models.UserRoles.LabelFor(role),
                        AllowedModels: allowedModels,
                        DefaultModel: row?.DefaultModel ?? "",
                        // SystemAdmin can never be restricted, so the editor shows
                        // its row read-only with the reason rather than a control
                        // that would always be refused.
                        CanRestrict: !Rbac.AccessPolicy.IsSuperUserRole(role),
                        UpdatedAtUtc: row?.UpdatedAtUtc,
                        Stale: stale);
                }).ToList();

                return Results.Ok(new { policies, installedModels = installed });
            })
            .WithSummary("Which models each role may run, plus the installed local models to choose from.");

            group.MapPut("/model-policies/{role}", async (
                string role, RoleModelPolicyInput input, HttpContext ctx, AppDbContext db,
                ChatService chat, RbacService svc, Rbac.ModelPolicy modelPolicy, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                if (!await svc.CanManageAsync(user, ct))
                    return Forbid(ManagersOnly);

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
                if (Rbac.AccessPolicy.IsSuperUserRole(canonical) && allowed.Count > 0)
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

                // Nothing is stale right after a save: the validation above just
                // confirmed every allowed model is installed.
                return Results.Ok(new RoleModelPolicyDto(
                    canonical, Models.UserRoles.LabelFor(canonical),
                    allowed, defaultModel,
                    !Rbac.AccessPolicy.IsSuperUserRole(canonical), row.UpdatedAtUtc,
                    new List<string>()));
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
            RbacOutcome.Invalid => Results.BadRequest(new { error = result.Error, invariant = result.Invariant }),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };

        /// <summary>
        /// 403 with a body. Results.Forbid() authenticates against a scheme this
        /// app has none of, so the status is written directly.
        /// </summary>
        private const string ManagersOnly = "Managing access requires SystemAdmin or the Manage access permission.";

        private static IResult Forbid(string? error) =>
            Results.Json(new { error = error ?? "Forbidden." }, statusCode: StatusCodes.Status403Forbidden);
    }
}
