// ম্যাট্রিক্স — HTTP surface, /api/matrix (docs/MATRIX_PROMPT.md §3.10).
//
//   POST /api/matrix/uploads                    multipart: files (.xlsx/.xlsm/.csv, ≤ 20 MB each)   action:matrix.upload
//   GET  /api/matrix/uploads                    recent uploads                                       section:matrix.data
//   GET  /api/matrix/uploads/{id}/profile       sheets, tables, columns, suggestions, quirks         section:matrix.data
//   POST /api/matrix/uploads/{id}/import        confirm mapping and template, import                 action:matrix.upload
//   GET  /api/matrix/templates                  saved layouts                                        action:matrix.templates
//   PUT  /api/matrix/templates/{id}             rename, change report kind                           action:matrix.templates
//   GET  /api/matrix/datasets                   imported datasets                                    section:matrix.data
//   GET  /api/matrix/datasets/{id}              one, with its columns                                section:matrix.data
//   GET  /api/matrix/datasets/{id}/rows         the viewer: ?offset&limit&filter&highlight&only      section:matrix.data
//   GET  /api/matrix/datasets/{id}/quality      its quality issues                                   section:matrix.data
//   POST /api/matrix/quality/{id}/fix           apply an issue's fix (raw kept, logged)              action:matrix.fix
//   GET  /api/matrix/catalog                    the field catalog                                    section:matrix.data
//
// The whole of /api/matrix is gated by the `matrix` tab in TabAccessMiddleware;
// each route asks AccessPolicy for its own section or action. No rule is
// decided here: MatrixService decides, these routes translate.

using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.DTOs.Matrix;
using RabbitIC.Api.Models;
using RabbitIC.Api.Rbac;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.MinimalApis
{
    public static class MatrixApis
    {
        public static IEndpointRouteBuilder MapMatrixApis(this IEndpointRouteBuilder app)
        {
            var g = app.MapGroup("/matrix");

            g.MapPost("/uploads", async (HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
            {
                if (await Caller(ctx, db, access, PermissionKeys.MatrixUpload, ct) is { } refused) return refused.Result;
                if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "Send the files as multipart/form-data." });
                var form = await ctx.Request.ReadFormAsync(ct);
                var files = new List<MxIncomingFile>();
                foreach (var f in form.Files)
                {
                    if (f.Length > IMatrixService.MaxUploadBytes)
                        return Results.BadRequest(new { error = $"{f.FileName} is larger than {IMatrixService.MaxUploadBytes / 1024 / 1024} MB." });
                    using var ms = new MemoryStream((int)f.Length);
                    await f.CopyToAsync(ms, ct);
                    files.Add(new MxIncomingFile(f.FileName, ms.ToArray()));
                }
                var result = await svc.UploadAsync(await User(ctx, db), files, ct);
                return result.Outcome == MxOutcome.Ok ? Results.Json(result.Value, statusCode: StatusCodes.Status201Created) : Error(result.Outcome, result.Error);
            }).DisableAntiforgery();

            g.MapGet("/uploads", async (HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixData, ct) is { } refused ? refused.Result
                    : Results.Ok(new { uploads = await svc.ListUploadsAsync(ct) }));

            g.MapGet("/uploads/{id:int}/profile", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixData, ct) is { } refused ? refused.Result : Map(await svc.GetProfileAsync(id, ct)));

            g.MapPost("/uploads/{id:int}/import", async (int id, MxImportInput? input, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixUpload, ct) is { } refused ? refused.Result
                    : Map(await svc.ImportAsync(await User(ctx, db), id, input ?? new MxImportInput(null), ct)));

            g.MapGet("/templates", async (HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixTemplates, ct) is { } refused ? refused.Result
                    : Results.Ok(new { templates = await svc.ListTemplatesAsync(ct) }));

            g.MapPut("/templates/{id:int}", async (int id, MxTemplateInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixTemplates, ct) is { } refused ? refused.Result
                    : Map(await svc.UpdateTemplateAsync(id, input, await User(ctx, db), ct)));

            g.MapGet("/datasets", async (HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixData, ct) is { } refused ? refused.Result
                    : Results.Ok(new { datasets = await svc.ListDatasetsAsync(ct) }));

            g.MapGet("/datasets/{id:int}", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixData, ct) is { } refused ? refused.Result : Map(await svc.GetDatasetAsync(id, ct)));

            g.MapGet("/datasets/{id:int}/rows", async (int id, int? offset, int? limit, string? filter, string? highlight, bool? only,
                HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixData, ct) is { } refused ? refused.Result
                    : Map(await svc.RowsAsync(await User(ctx, db), id, offset ?? 0, limit ?? 200, filter, highlight, only ?? highlight is not null, ct)));

            g.MapGet("/datasets/{id:int}/quality", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixData, ct) is { } refused ? refused.Result : Map(await svc.IssuesAsync(id, ct)));

            g.MapPost("/quality/{id:int}/fix", async (int id, MxFixInput? input, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixFix, ct) is { } refused ? refused.Result
                    : Map(await svc.FixAsync(await User(ctx, db), id, input ?? new MxFixInput(null), ct)));

            g.MapGet("/catalog", async (HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixData, ct) is { } refused ? refused.Result
                    : Results.Ok(new { fields = svc.Catalog() }));

            return app;
        }

        private sealed record Refusal(IResult Result);

        /// <summary>401 without a session; 403 naming the permission when AccessPolicy says no.</summary>
        private static async Task<Refusal?> Caller(HttpContext ctx, AppDbContext db, AccessPolicy access, string key, CancellationToken ct)
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) return new Refusal(Results.Unauthorized());
            if (!await access.HasAsync(user, key, ct))
                return new Refusal(Results.Json(new { error = $"This needs {PermissionCatalog.Find(key)?.En ?? key} ({key})." }, statusCode: StatusCodes.Status403Forbidden));
            ctx.Items[UserKey] = user;
            return null;
        }

        private const string UserKey = "matrix:user";

        private static async Task<User> User(HttpContext ctx, AppDbContext db) =>
            ctx.Items[UserKey] as User ?? (await Authorization.ResolveUser(ctx, db))!;

        private static IResult Map<T>(MxResult<T> result) => result.Outcome == MxOutcome.Ok ? Results.Ok(result.Value) : Error(result.Outcome, result.Error);

        private static IResult Error(MxOutcome outcome, string? error) => outcome switch
        {
            MxOutcome.NotFound => Results.NotFound(new { error }),
            MxOutcome.Forbidden => Results.Json(new { error }, statusCode: StatusCodes.Status403Forbidden),
            MxOutcome.Gone => Results.Json(new { error }, statusCode: StatusCodes.Status410Gone),
            _ => Results.BadRequest(new { error }),
        };
    }
}
