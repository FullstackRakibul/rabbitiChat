// HTTP surface for the component splitter.
//
// One endpoint per pipeline stage rather than one "split it" call, because the
// human approval gate is mandatory: /analyse produces a proposal and NOTHING
// else, and /generate is reachable only with a plan the user approved. There is
// deliberately no endpoint that goes from source code to files in one call —
// the gate cannot be skipped by an API consumer either.
//
// The whole surface sits behind the "component-splitter" tab in
// TabAccessMiddleware, so a role without the tab is refused before any handler
// runs. No endpoint here touches the filesystem: files are returned in the
// response body for download, never written to a repo.

using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.ComponentSplitter;
using RabbitIC.Api.ComponentSplitter.Generation;
using RabbitIC.Api.ComponentSplitter.Ir;
using RabbitIC.Api.ComponentSplitter.Parsing;
using RabbitIC.Api.Data;

namespace RabbitIC.Api.MinimalApis;

/// <summary>Body of POST /component-splitter/parse and /analyse.</summary>
/// <param name="ComponentTs">The component's .ts source.</param>
/// <param name="TemplateHtml">Separate template contents, when templateUrl was used.</param>
/// <param name="Model">Model name for the AI stage (ignored by /parse and /facts).</param>
/// <param name="Source">Model source key.</param>
public record SplitAnalyseRequest(
    string? ComponentTs, string? TemplateHtml, string? Model, string? Source);

/// <summary>
/// Body of POST /component-splitter/generate.
///
/// Carries the ORIGINAL SOURCE alongside the approved plan so the server
/// re-parses to a fresh IR rather than trusting a client-held one. The IR is
/// ground truth; accepting it from the client would let a crafted request put
/// anything it liked into "facts".
/// </summary>
public record SplitGenerateRequest(
    string? ComponentTs, string? TemplateHtml, ApprovedPlan? Plan);

public static class ComponentSplitterApis
{
    public static IEndpointRouteBuilder MapComponentSplitterApis(this IEndpointRouteBuilder app)
    {
        // ---- Stage 1: parse → IR ----
        // Separate from /analyse so the UI can show the parse result (and a
        // rejection) before any model call is made or any token is spent.
        app.MapPost("/component-splitter/parse", async (
            SplitAnalyseRequest req, HttpContext ctx, AppDbContext db, SplitterService splitter) =>
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) return Results.Unauthorized();

            try
            {
                var ir = splitter.Parse(req.ComponentTs ?? string.Empty, req.TemplateHtml);
                return Results.Ok(Describe(ir));
            }
            catch (NotAngularException ex)
            {
                // A clear, actionable rejection — v1 is Angular-only by design.
                return Results.BadRequest(new { error = ex.Message, rejected = true });
            }
        });

        // ---- Stage 2a alone: deterministic facts ----
        // Exposed on its own because "identical across repeated runs" is a
        // property worth being able to check directly, with no model involved.
        app.MapPost("/component-splitter/facts", async (
            SplitAnalyseRequest req, HttpContext ctx, AppDbContext db, SplitterService splitter) =>
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) return Results.Unauthorized();

            try
            {
                var ir = splitter.Parse(req.ComponentTs ?? string.Empty, req.TemplateHtml);
                var facts = SplitterService.DeriveFacts(ir);

                return Results.Ok(new
                {
                    component = Describe(ir),
                    candidates = facts,
                });
            }
            catch (NotAngularException ex)
            {
                return Results.BadRequest(new { error = ex.Message, rejected = true });
            }
        });

        // ---- Stages 2a ‖ 2b → 3: the merged proposal, streamed ----
        // NDJSON, matching the chat stream's wire format, so the client reuses
        // its existing reader. Frames: stage / ai / proposal / error.
        app.MapPost("/component-splitter/analyse", async (
            SplitAnalyseRequest req, HttpContext ctx, AppDbContext db,
            SplitterService splitter, RabbitIC.Api.Rbac.ModelPolicy modelPolicy,
            CancellationToken ct) =>
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.Model))
                return Results.BadRequest(new { error = "A model is required for the reasoning stage." });

            // The model policy, before the reasoning stage spends anything.
            var policy = await modelPolicy.CheckAsync(user, req.Model, req.Source, ct: ct);
            if (!policy.Allowed)
                return Results.Json(new { error = policy.Error }, statusCode: StatusCodes.Status403Forbidden);

            ComponentIr ir;
            try
            {
                ir = splitter.Parse(req.ComponentTs ?? string.Empty, req.TemplateHtml);
            }
            catch (NotAngularException ex)
            {
                // Rejected before streaming starts, so this can still be a clean
                // 400 rather than an in-band error frame.
                return Results.BadRequest(new { error = ex.Message, rejected = true });
            }

            ctx.Response.ContentType = "application/x-ndjson";
            var writer = new SplitterStreamWriter(ctx.Response, ct);

            await writer.StageAsync("parse", "done", $"{ir.AllNodes().Count()} nodes");
            await writer.ComponentAsync(Describe(ir));
            await writer.StageAsync("facts", "running");
            await writer.StageAsync("reason", "running");

            try
            {
                var proposal = await splitter.AnalyseAsync(
                    ir, req.Model!, req.Source,
                    onAiDelta: text => writer.AiDeltaAsync(text),
                    ct);

                await writer.StageAsync("facts", "done");
                await writer.StageAsync("reason", proposal.AiFailed ? "failed" : "done");
                await writer.StageAsync("merge", "done",
                    $"{proposal.Components.Count} component(s) proposed");

                await writer.ProposalAsync(proposal);
            }
            catch (OperationCanceledException)
            {
                // The client navigated away; nothing to report.
            }
            catch (Exception ex)
            {
                await writer.ErrorAsync(ex.Message);
            }

            return Results.Empty;
        });

        // ---- Stages 5 + 6: generate, then validate ----
        // The two always travel together: a caller cannot obtain generated files
        // without the report that says whether they are correct.
        app.MapPost("/component-splitter/generate", async (
            SplitGenerateRequest req, HttpContext ctx, AppDbContext db, SplitterService splitter) =>
        {
            var user = await Authorization.ResolveUser(ctx, db);
            if (user is null) return Results.Unauthorized();

            if (req.Plan is null || req.Plan.Components.Count == 0)
                return Results.BadRequest(new
                {
                    error = "An approved plan with at least one component is required.",
                });

            try
            {
                // Re-parsed server-side: the IR is ground truth and is never
                // accepted from the client.
                var ir = splitter.Parse(req.ComponentTs ?? string.Empty, req.TemplateHtml);
                var (generated, report) = SplitterService.GenerateAndValidate(ir, req.Plan);

                return Results.Ok(new
                {
                    files = generated.Files,
                    parentTemplate = generated.ParentTemplate,
                    validation = report,
                    // Stated explicitly so the client never infers correctness
                    // from the call having succeeded.
                    generationSucceeded = true,
                    validationPassed = report.Passed,
                });
            }
            catch (NotAngularException ex)
            {
                return Results.BadRequest(new { error = ex.Message, rejected = true });
            }
        });

        return app;
    }

    /// <summary>
    /// The IR as the UI shows it. Deliberately a projection rather than the IR
    /// itself: the full node tree with every source offset is large and the UI
    /// needs only the summary.
    /// </summary>
    private static object Describe(ComponentIr ir) => new
    {
        className = ir.ClassName,
        selector = ir.Selector,
        wiring = ir.Wiring == WiringKind.Standalone ? "standalone" : "ngmodule",
        uiKit = ir.UiKit,
        templateIsInline = ir.TemplateIsInline,
        nodeCount = ir.AllNodes().Count(),
        bindingCount = ir.AllNodes().Sum(n => n.Bindings.Count),
        members = ir.Members,
        template = ir.TemplateSource,
    };
}
