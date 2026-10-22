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
//   POST /api/matrix/query                      run a MatrixQuery { query, explain? }                 section:matrix.dashboards
//   GET  /api/matrix/kpis                       the KPI library and thresholds                       section:matrix.dashboards
//   PUT  /api/matrix/kpis/{key}                 names, formula words, thresholds                     action:matrix.kpis.edit
//   POST /api/matrix/kpis/{key}/run             { dataset?, previous?, against?, params? }           section:matrix.dashboards
//   GET  /api/matrix/datasets/{id}/card         the report card                                      section:matrix.dashboards
//   POST /api/matrix/datasets/{id}/report       { filters, compare } → the Report page's charts      section:matrix.dashboards
//   GET  /api/matrix/datasets/{id}/summary      ?lang=bn|en — the saved summary                     section:matrix.dashboards
//   POST /api/matrix/datasets/{id}/summary      { language, model? } — written from the card, checked section:matrix.dashboards
//   POST /api/matrix/chat                       { message, datasetId?, conversationId? } → NDJSON (no datasetId: every current dataset)  section:matrix.ask
//   POST /api/matrix/datasets/{id}/warm         the plan prefix read into the model's cache (≤ 1 per 10 min) section:matrix.ask
//   POST /api/matrix/ask/warm                   the same, for Ask across every current dataset        section:matrix.ask
//   GET  /api/matrix/conversations              ?dataset= — this person's Ask conversations; without it, the Ask section's (no dataset)  section:matrix.ask
//   GET/DELETE /api/matrix/conversations/{id}   its messages (with what each answer used) / delete   section:matrix.ask
//   GET  /api/matrix/dashboards                 the dashboards the caller may see                    section:matrix.dashboards
//   GET  /api/matrix/dashboards/{id}            one, with its layout                                 section:matrix.dashboards
//   POST /api/matrix/dashboards/{id}/run        { filters, compare } → every widget's numbers        section:matrix.dashboards
//   POST /api/matrix/dashboards                 create (or copy)                                     action:matrix.dashboards.edit
//   PUT  /api/matrix/dashboards/{id}            rename, lay out, share                               action:matrix.dashboards.edit
//   DELETE /api/matrix/dashboards/{id}          delete (not an automatic one)                        action:matrix.dashboards.edit
//   GET  /api/matrix/export/{xlsx|csv}/{id}     a dataset, without sensitive columns                 section:matrix.data
//   GET  /api/matrix/export/dashboard/{id}      a dashboard's layout as JSON                         section:matrix.data
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

            g.MapPost("/query", async (MxQueryRequest input, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixDashboards, ct) is { } refused ? refused.Result
                    : Map(await svc.QueryAsync(await User(ctx, db), input.Query, input.Explain == true, ct)));

            g.MapGet("/kpis", async (HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixDashboards, ct) is { } refused ? refused.Result
                    : Results.Ok(new { kpis = await svc.ListKpisAsync(ct) }));

            g.MapPut("/kpis/{key}", async (string key, MxKpiInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixKpisEdit, ct) is { } refused ? refused.Result : Map(await svc.UpdateKpiAsync(key, input, ct)));

            g.MapPost("/kpis/{key}/run", async (string key, MxKpiRunInput? input, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixDashboards, ct) is { } refused ? refused.Result
                    : Map(await svc.RunKpiAsync(await User(ctx, db), key, input ?? new MxKpiRunInput(null, null, null, null), ct)));

            g.MapGet("/datasets/{id:int}/card", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixDashboards, ct) is { } refused ? refused.Result : Map(await svc.CardAsync(await User(ctx, db), id, ct)));

            g.MapPost("/datasets/{id:int}/report", async (int id, MxDashboardRunInput? input, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixDashboards, ct) is { } refused ? refused.Result
                    : Map(await svc.RunReportAsync(await User(ctx, db), id, input ?? new MxDashboardRunInput(null), ct)));

            g.MapGet("/datasets/{id:int}/summary", async (int id, string? lang, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixDashboards, ct) is { } refused ? refused.Result
                    : Map(await svc.SummaryAsync(await User(ctx, db), id, lang ?? "bn", ct)));

            g.MapPost("/datasets/{id:int}/summary", async (int id, MxSummaryInput? input, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixDashboards, ct) is { } refused ? refused.Result
                    : Map(await svc.WriteSummaryAsync(await User(ctx, db), id, input ?? new MxSummaryInput(null, null), ct)));

            // ---- Ask (§3.9): streamed NDJSON frames — status, context, content, formula, unverified, done ----

            g.MapPost("/chat", async (MxAskInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc) =>
            {
                var ct = ctx.RequestAborted;
                if (await Caller(ctx, db, access, PermissionKeys.MatrixAsk, ct) is { } refused) { await refused.Result.ExecuteAsync(ctx); return; }
                var sink = new AskSink(ctx);
                var r = await svc.AskAsync(await User(ctx, db), input, sink, ct);
                if (r.Outcome != MxOutcome.Ok && !sink.Started) await Error(r.Outcome, r.Error).ExecuteAsync(ctx);
            });

            g.MapPost("/datasets/{id:int}/warm", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixAsk, ct) is { } refused ? refused.Result
                    : Map(await svc.WarmAsync(await User(ctx, db), id, ct)));

            g.MapPost("/ask/warm", async (HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixAsk, ct) is { } refused ? refused.Result
                    : Map(await svc.WarmAsync(await User(ctx, db), null, ct)));

            g.MapGet("/conversations", async (int? dataset, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixAsk, ct) is { } refused ? refused.Result
                    : Results.Ok(new { conversations = await svc.ListConversationsAsync(await User(ctx, db), dataset, ct) }));

            g.MapGet("/conversations/{id:int}", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixAsk, ct) is { } refused ? refused.Result
                    : Map(await svc.ConversationAsync(await User(ctx, db), id, ct)));

            g.MapDelete("/conversations/{id:int}", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
            {
                if (await Caller(ctx, db, access, PermissionKeys.MatrixAsk, ct) is { } refused) return refused.Result;
                var r = await svc.DeleteConversationAsync(await User(ctx, db), id, ct);
                return r.Outcome == MxOutcome.Ok ? Results.NoContent() : Error(r.Outcome, r.Error);
            });

            g.MapGet("/catalog", async (HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixData, ct) is { } refused ? refused.Result
                    : Results.Ok(new { fields = svc.Catalog() }));

            // ---- dashboards (§3.8): reading needs the section, every change the edit action ----

            g.MapGet("/dashboards", async (HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixDashboards, ct) is { } refused ? refused.Result
                    : Results.Ok(new { dashboards = await svc.ListDashboardsAsync(await User(ctx, db), ct) }));

            g.MapGet("/dashboards/{id:int}", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixDashboards, ct) is { } refused ? refused.Result
                    : Map(await svc.GetDashboardAsync(await User(ctx, db), id, ct)));

            g.MapPost("/dashboards/{id:int}/run", async (int id, MxDashboardRunInput? input, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixDashboards, ct) is { } refused ? refused.Result
                    : Map(await svc.RunDashboardAsync(await User(ctx, db), id, input ?? new MxDashboardRunInput(null), ct)));

            g.MapPost("/dashboards", async (MxDashboardInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
            {
                if (await Caller(ctx, db, access, PermissionKeys.MatrixDashboardsEdit, ct) is { } refused) return refused.Result;
                var r = await svc.CreateDashboardAsync(await User(ctx, db), input, ct);
                return r.Outcome == MxOutcome.Ok ? Results.Json(r.Value, statusCode: StatusCodes.Status201Created) : Error(r.Outcome, r.Error);
            });

            g.MapPut("/dashboards/{id:int}", async (int id, MxDashboardInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.MatrixDashboardsEdit, ct) is { } refused ? refused.Result
                    : Map(await svc.UpdateDashboardAsync(await User(ctx, db), id, input, ct)));

            g.MapDelete("/dashboards/{id:int}", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
            {
                if (await Caller(ctx, db, access, PermissionKeys.MatrixDashboardsEdit, ct) is { } refused) return refused.Result;
                var r = await svc.DeleteDashboardAsync(await User(ctx, db), id, ct);
                return r.Outcome == MxOutcome.Ok ? Results.NoContent() : Error(r.Outcome, r.Error);
            });

            // ---- export: a dataset as xlsx or csv, or a dashboard's layout ----

            g.MapGet("/export/{kind}/{id:int}", async (string kind, int id, HttpContext ctx, AppDbContext db, AccessPolicy access, IMatrixService svc, CancellationToken ct) =>
            {
                if (await Caller(ctx, db, access, PermissionKeys.MatrixData, ct) is { } refused) return refused.Result;
                var r = await svc.ExportAsync(await User(ctx, db), kind, id, ct);
                if (r.Outcome != MxOutcome.Ok) return Error(r.Outcome, r.Error);
                return Results.File(r.Value!.Bytes, r.Value.ContentType, r.Value.FileName);
            });

            return app;
        }

        private sealed record Refusal(IResult Result);

        /// <summary>An Ask answer's stream: headers go out with the first frame, once the question is accepted.</summary>
        private sealed class AskSink(HttpContext ctx) : IMxAskSink
        {
            private ChatStreamWriter? _writer;
            public bool Started => _writer is not null;

            public Task StartAsync(int conversationId)
            {
                ctx.Response.Headers["X-Conversation-Id"] = conversationId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
                _writer = new ChatStreamWriter(ctx.Response, ctx.RequestAborted);
                return Task.CompletedTask;
            }

            public Task StatusAsync(string text) => _writer!.FrameAsync(new { type = "status", text });
            public Task FrameAsync(object frame) => _writer!.FrameAsync(frame);
            public Task ContentAsync(string text) => _writer!.ContentAsync(text);
        }

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
