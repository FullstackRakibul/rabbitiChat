// এইচআর ওয়ার্কস্পেস · HRIS Workspace — HTTP surface, /api/hris (docs/HRIS_WORKSPACE_PROMPT.md §3.5).
//
//   GET    /api/hris/pins?board=shared|personal   the board's pins                          section:hris.reports
//   POST   /api/hris/pins                         add a pin                                 section:hris.reports + shared: action:hris.pin
//   PUT    /api/hris/pins/{id}                    retitle, resize, change                   section:hris.reports + shared: action:hris.pin; personal: owner
//   DELETE /api/hris/pins/{id}                    remove                                    the same
//   PUT    /api/hris/pins/order                   { board, ids }                            the same
//   POST   /api/hris/pins/render                  { board, ids?, filters } → every pin's numbers (Matrix's widgets)   section:hris.reports
//   GET    /api/hris/status                       the data status card                      tab:hris
//   GET    /api/hris/export/{pin|board}?board=&pin=&filters=<json>   .xlsx                  action:hris.export
//
// Mapped only when Hris:Enabled is on (otherwise every path is a 404). Not in
// TabAccessMiddleware's table: every route asks AccessPolicy for a key whose
// parent chain ends at tab:hris, so the tab is checked on each of them. What a pin
// shows is still decided by Matrix for this user (salary, personal, sensitive).

using System.Text.Json;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Hris;
using RabbitIC.Api.Models;
using RabbitIC.Api.Rbac;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.MinimalApis
{
    public static class HrisApis
    {
        public static IEndpointRouteBuilder MapHrisApis(this IEndpointRouteBuilder app)
        {
            var g = app.MapGroup("/hris");

            g.MapGet("/pins", async (string? board, HttpContext ctx, AppDbContext db, AccessPolicy access, HrisService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.HrisReports, ct) is var (user, refused) && refused is not null ? refused
                    : Map(await svc.ListAsync(user!, board, ct)));

            g.MapPost("/pins", async (HrisPinInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, HrisService svc, CancellationToken ct) =>
            {
                var (user, refused) = await Caller(ctx, db, access, PermissionKeys.HrisReports, ct);
                if (refused is not null) return refused;
                var r = await svc.CreateAsync(user!, input, ct);
                return r.Outcome == MxOutcome.Ok ? Results.Json(r.Value, statusCode: StatusCodes.Status201Created) : Error(r.Outcome, r.Error);
            });

            g.MapPut("/pins/order", async (HrisOrderInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, HrisService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.HrisReports, ct) is var (user, refused) && refused is not null ? refused
                    : Map(await svc.OrderAsync(user!, input, ct)));

            g.MapPut("/pins/{id:int}", async (int id, HrisPinInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, HrisService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.HrisReports, ct) is var (user, refused) && refused is not null ? refused
                    : Map(await svc.UpdateAsync(user!, id, input, ct)));

            g.MapDelete("/pins/{id:int}", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, HrisService svc, CancellationToken ct) =>
            {
                var (user, refused) = await Caller(ctx, db, access, PermissionKeys.HrisReports, ct);
                if (refused is not null) return refused;
                var r = await svc.DeleteAsync(user!, id, ct);
                return r.Outcome == MxOutcome.Ok ? Results.NoContent() : Error(r.Outcome, r.Error);
            });

            g.MapPost("/pins/render", async (HrisRenderInput? input, HttpContext ctx, AppDbContext db, AccessPolicy access, HrisService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.HrisReports, ct) is var (user, refused) && refused is not null ? refused
                    : Map(await svc.RenderAsync(user!, input ?? new HrisRenderInput(null, null, null), ct)));

            g.MapGet("/status", async (HttpContext ctx, AppDbContext db, AccessPolicy access, HrisService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabHris, ct) is var (user, refused) && refused is not null ? refused
                    : Results.Ok(await svc.StatusAsync(user!, ct)));

            g.MapGet("/export/{kind}", async (string kind, string? board, int? pin, string? filters, HttpContext ctx, AppDbContext db,
                AccessPolicy access, HrisService svc, CancellationToken ct) =>
            {
                var (user, refused) = await Caller(ctx, db, access, PermissionKeys.HrisExport, ct);
                if (refused is not null) return refused;
                HrisFilters? f = null;
                if (!string.IsNullOrWhiteSpace(filters))
                    try { f = JsonSerializer.Deserialize<HrisFilters>(filters, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
                    catch (JsonException) { return Results.BadRequest(new { error = "filters is the filter bar as JSON." }); }
                var r = await svc.ExportAsync(user!, kind, board, pin, f, ct);
                if (r.Outcome != MxOutcome.Ok) return Error(r.Outcome, r.Error);
                ctx.Response.Headers["X-Export-Headers"] = Uri.EscapeDataString(string.Join("|", r.Value!.Headers));
                return Results.File(r.Value.Bytes, r.Value.ContentType, r.Value.FileName);
            });

            return app;
        }

        /// <summary>401 without a session; 403 naming the permission when AccessPolicy says no.</summary>
        private static async Task<(User? User, IResult? Refused)> Caller(HttpContext ctx, AppDbContext db, AccessPolicy access, string key, CancellationToken ct)
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) return (null, Results.Unauthorized());
            if (!await access.HasAsync(user, key, ct))
                return (user, Results.Json(new { error = $"This needs {PermissionCatalog.Find(key)?.En ?? key} ({key})." }, statusCode: StatusCodes.Status403Forbidden));
            return (user, null);
        }

        private static IResult Map<T>(MxResult<T> result) => result.Outcome == MxOutcome.Ok ? Results.Ok(result.Value) : Error(result.Outcome, result.Error);

        private static IResult Error(MxOutcome outcome, string? error) => outcome switch
        {
            MxOutcome.NotFound => Results.NotFound(new { error }),
            MxOutcome.Forbidden => Results.Json(new { error }, statusCode: StatusCodes.Status403Forbidden),
            _ => Results.BadRequest(new { error }),
        };
    }
}
