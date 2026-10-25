// সত্য মন্ত্রণালয় — the staff API, /api/truth (docs/TRUTH_PROMPT.md §8).
//
// Two permissions, kept minimal: tab:truth opens the tab and edits drafts;
// action:truth.publish puts anything in front of the public.
//
//   GET  /api/truth/techs                          technologies                               tab:truth
//   POST /api/truth/techs · PUT /techs/{id}        add / edit one                             tab:truth
//   GET  /api/truth/issues                         every issue, newest week first             tab:truth
//   POST /api/truth/issues                         create a draft                             tab:truth
//   GET  /api/truth/issues/{id}                    the issue with everything under it         tab:truth
//   PUT  /api/truth/issues/{id} · DELETE           edit / delete (never once published)       tab:truth
//   GET  /api/truth/issues/{id}/check              the pre-publish checklist + number check   tab:truth
//   GET  /api/truth/issues/{id}/preview            the snapshot publishing would write        tab:truth
//   POST /api/truth/issues/{id}/publish            checklist, then a snapshot                 action:truth.publish
//   POST /api/truth/issues/{id}/withdraw           off the public site                        action:truth.publish
//   POST /api/truth/issues/{id}/items              add an item                                tab:truth
//   PUT  /api/truth/items/{id} · DELETE            save the whole item / delete (unpublished) tab:truth
//   POST /api/truth/items/{id}/status              a status change, logged                    tab:truth
//   POST /api/truth/items/{id}/corrections         a visible correction                       tab:truth
//   POST /api/truth/items/{id}/verify              a number verified by hand, with a note     tab:truth
//   DELETE /api/truth/items/{id}/verify?token=     undo that                                  tab:truth
//   GET  /api/truth/content/{kind}                 site | memes | glossary: default, schema,  action:truth.publish
//                                                  published, draft, history
//   POST /api/truth/content/{kind}/validate        { text } → errors                          action:truth.publish
//   PUT  /api/truth/content/{kind}/draft           { text } → saved, errors                   action:truth.publish
//   POST /api/truth/content/{kind}/publish         { text, note } → the next version          action:truth.publish
//   POST /api/truth/content/{kind}/rollback/{v}    that version, published again              action:truth.publish
//   GET  /api/truth/platforms                      platforms.json: what a guide may declare   tab:truth
//   POST /api/truth/guides/render                  { template, values } → { text, errors }    tab:truth
//   GET  /api/truth/guides · POST                  every guide / a new draft                  tab:truth
//   GET  /api/truth/guides/{id} · PUT · DELETE     one, whole / save it / delete (unpublished) tab:truth
//   POST /api/truth/guides/{id}/grid               every variant rendered and checked; records that it was opened  tab:truth
//   GET  /api/truth/guides/{id}/check              what blocks publishing, and the number check  tab:truth
//   POST /api/truth/guides/{id}/verify · DELETE    a number verified by hand / undo that      tab:truth
//   POST /api/truth/guides/{id}/corrections        a visible correction                       tab:truth
//   POST /api/truth/guides/{id}/ack                { findingKey, note }: a warning acknowledged  action:truth.publish
//   PUT  /api/truth/guides/{id}/checklist          the publisher's 4 ticks                    action:truth.publish
//   POST /api/truth/guides/{id}/publish · withdraw                                            action:truth.publish
//
// The whole of /api/truth is gated by the `truth` tab in TabAccessMiddleware;
// each route asks AccessPolicy for its own key. TruthService decides.

using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.DTOs.Truth;
using RabbitIC.Api.Models;
using RabbitIC.Api.Rbac;
using RabbitIC.Api.Truth;

namespace RabbitIC.Api.MinimalApis
{
    public static class TruthApis
    {
        public static IEndpointRouteBuilder MapTruthApis(this IEndpointRouteBuilder app)
        {
            var g = app.MapGroup("/truth");

            g.MapGet("/techs", async (HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Results.Ok(new { techs = await svc.ListTechsAsync(ct) }));
            g.MapPost("/techs", async (TechInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await svc.SaveTechAsync(null, input, ct), created: true));
            g.MapPut("/techs/{id:int}", async (int id, TechInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await svc.SaveTechAsync(id, input, ct)));

            g.MapGet("/issues", async (HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Results.Ok(new { issues = await svc.ListIssuesAsync(ct) }));
            g.MapPost("/issues", async (IssueInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await svc.SaveIssueAsync(null, input, Me(ctx), ct), created: true));
            g.MapGet("/issues/{id:int}", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await svc.GetIssueAsync(id, ct)));
            g.MapPut("/issues/{id:int}", async (int id, IssueInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await svc.SaveIssueAsync(id, input, Me(ctx), ct)));
            g.MapDelete("/issues/{id:int}", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await svc.DeleteIssueAsync(id, ct)));
            g.MapGet("/issues/{id:int}/check", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await svc.CheckAsync(id, ct)));
            g.MapGet("/issues/{id:int}/preview", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await svc.PreviewAsync(id, ct)));
            g.MapPost("/issues/{id:int}/publish", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TruthPublish, ct) is { } refused ? refused : Map(await svc.PublishAsync(id, Me(ctx), ct)));
            g.MapPost("/issues/{id:int}/withdraw", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TruthPublish, ct) is { } refused ? refused : Map(await svc.WithdrawAsync(id, ct)));

            g.MapPost("/issues/{id:int}/items", async (int id, ItemInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await svc.CreateItemAsync(id, input, Me(ctx), ct), created: true));
            g.MapPut("/items/{id:int}", async (int id, ItemInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await svc.UpdateItemAsync(id, input, ct)));
            g.MapDelete("/items/{id:int}", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await svc.DeleteItemAsync(id, ct)));
            g.MapPost("/items/{id:int}/status", async (int id, StatusInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await svc.ChangeStatusAsync(id, input, Me(ctx), ct)));
            g.MapPost("/items/{id:int}/corrections", async (int id, CorrectionInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await svc.AddCorrectionAsync(id, input, Me(ctx), ct), created: true));
            g.MapPost("/items/{id:int}/verify", async (int id, VerifyInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await svc.VerifyNumberAsync(id, input, Me(ctx), ct)));
            g.MapDelete("/items/{id:int}/verify", async (int id, string token, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await svc.UnverifyNumberAsync(id, token, ct)));

            g.MapGet("/platforms", async (HttpContext ctx, AppDbContext db, AccessPolicy access, GuideService guides, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Results.Ok(guides.Platforms()));
            var gd = g.MapGroup("/guides");
            gd.MapPost("/render", async (RenderInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, CancellationToken ct) =>
            {
                if (await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused) return refused;
                var r = GuideTemplate.Render(input.Template, input.Values ?? new Dictionary<string, string>());
                return Results.Ok(new { text = r.Text, errors = r.Errors });
            });
            gd.MapGet("", async (HttpContext ctx, AppDbContext db, AccessPolicy access, GuideService guides, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Results.Ok(new { guides = await guides.ListAsync(ct) }));
            gd.MapPost("", async (GuideInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, GuideService guides, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await guides.SaveAsync(null, input, Me(ctx), ct), created: true));
            gd.MapGet("/{id:int}", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, GuideService guides, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await guides.GetAsync(id, ct)));
            gd.MapPut("/{id:int}", async (int id, GuideInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, GuideService guides, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await guides.SaveAsync(id, input, Me(ctx), ct)));
            gd.MapDelete("/{id:int}", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, GuideService guides, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await guides.DeleteAsync(id, ct)));
            gd.MapPost("/{id:int}/grid", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, GuideService guides, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await guides.GridAsync(id, ct)));
            gd.MapGet("/{id:int}/check", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, GuideService guides, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await guides.CheckAsync(id, ct)));
            gd.MapPost("/{id:int}/verify", async (int id, VerifyInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, GuideService guides, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await guides.VerifyNumberAsync(id, input, Me(ctx), ct)));
            gd.MapPost("/{id:int}/corrections", async (int id, CorrectionInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, GuideService guides, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TabTruth, ct) is { } refused ? refused : Map(await guides.AddCorrectionAsync(id, input, Me(ctx), ct), created: true));
            gd.MapPost("/{id:int}/ack", async (int id, AckInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, GuideService guides, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TruthPublish, ct) is { } refused ? refused : Map(await guides.AcknowledgeAsync(id, input, Me(ctx), ct)));
            gd.MapPut("/{id:int}/checklist", async (int id, ChecklistInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, GuideService guides, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TruthPublish, ct) is { } refused ? refused : Map(await guides.SetChecklistAsync(id, input, Me(ctx), ct)));
            gd.MapPost("/{id:int}/publish", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, GuideService guides, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TruthPublish, ct) is { } refused ? refused : Map(await guides.PublishAsync(id, Me(ctx), ct)));
            gd.MapPost("/{id:int}/withdraw", async (int id, HttpContext ctx, AppDbContext db, AccessPolicy access, GuideService guides, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TruthPublish, ct) is { } refused ? refused : Map(await guides.WithdrawAsync(id, ct)));

            var c = g.MapGroup("/content/{kind}");
            c.MapGet("", async (string kind, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TruthPublish, ct) is { } refused ? refused : Map(await svc.ContentStateAsync(kind, ct)));
            c.MapPost("/validate", async (string kind, SiteTextInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TruthPublish, ct) is { } refused ? refused
                    : TruthContent.IsKind(kind) ? Results.Ok(new { errors = TruthContent.Validate(kind, input.Text) }) : Results.NotFound(new { error = $"No content kind {kind}." }));
            c.MapPut("/draft", async (string kind, SiteTextInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TruthPublish, ct) is { } refused ? refused : Map(await svc.SaveContentDraftAsync(kind, input.Text, Me(ctx), ct)));
            c.MapPost("/publish", async (string kind, SiteTextInput input, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TruthPublish, ct) is { } refused ? refused : Map(await svc.PublishContentAsync(kind, input.Text, input.Note, Me(ctx), ct)));
            c.MapPost("/rollback/{version:int}", async (string kind, int version, HttpContext ctx, AppDbContext db, AccessPolicy access, TruthService svc, CancellationToken ct) =>
                await Caller(ctx, db, access, PermissionKeys.TruthPublish, ct) is { } refused ? refused : Map(await svc.RollbackContentAsync(kind, version, Me(ctx), ct)));

            return app;
        }

        private const string UserKey = "truth:user";

        /// <summary>401 without a session; 403 naming the permission when AccessPolicy says no.</summary>
        private static async Task<IResult?> Caller(HttpContext ctx, AppDbContext db, AccessPolicy access, string key, CancellationToken ct)
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) return Results.Unauthorized();
            if (!await access.HasAsync(user, key, ct))
                return Results.Json(new { error = $"This needs {PermissionCatalog.Find(key)?.En ?? key} ({key})." }, statusCode: StatusCodes.Status403Forbidden);
            ctx.Items[UserKey] = user;
            return null;
        }

        private static User Me(HttpContext ctx) => (User)ctx.Items[UserKey]!;

        private static IResult Map<T>(TrResult<T> r, bool created = false) => r.Outcome switch
        {
            TrOutcome.Ok => created ? Results.Json(r.Value, statusCode: StatusCodes.Status201Created) : Results.Ok(r.Value),
            TrOutcome.NotFound => Results.NotFound(new { error = r.Error }),
            TrOutcome.Conflict => Results.Conflict(new { error = r.Error }),
            _ => Results.BadRequest(new { error = r.Error, problems = r.Problems }),
        };
    }
}
