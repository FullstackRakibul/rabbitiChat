// PO Studio's own endpoints. Phase 1 contributes one: a preview of the assembled
// prompt, which is how the Profiles screen shows a merchandiser what the model
// will actually be told, and how the eval checks the builder's determinism and
// its budget without going near a model.
//
// Gated by the `docs` tab in TabAccessMiddleware, like /api/documents and
// /api/buyer-profiles — the prompt carries the buyer's own rules, so it is not
// more public than the documents it was built for.

using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Extraction;

namespace RabbitIC.Api.MinimalApis
{
    /// <param name="Buyer">Profile name. Unknown or absent means no profile knowledge.</param>
    /// <param name="DocumentId">Use this document's text instead of DocumentText.</param>
    /// <param name="NumCtxOverride">
    /// A smaller window, so the Profiles screen (and the eval) can see which parts
    /// the budget would drop without changing the install's setting.
    /// </param>
    public record PromptPreviewRequest(
        string? Buyer, string? DocumentText, int? DocumentId, int? NumCtxOverride);

    public static class PoStudioApis
    {
        public static IEndpointRouteBuilder MapPoStudioApis(this IEndpointRouteBuilder app)
        {
            // What the model will be told, assembled by the same pure builder the
            // extraction path uses — so a preview can never drift from the real run.
            app.MapPost("/po-studio/prompt-preview", async (
                PromptPreviewRequest req, HttpContext ctx, AppDbContext db,
                IConfiguration config, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var profile = string.IsNullOrWhiteSpace(req.Buyer)
                    ? null
                    : await db.BuyerProfiles.FirstOrDefaultAsync(p => p.Name == req.Buyer, ct);

                var text = req.DocumentText ?? "";
                if (req.DocumentId is int docId)
                {
                    var doc = await db.Documents
                        .FirstOrDefaultAsync(d => d.Id == docId && d.OwnerUserId == user.Id, ct);
                    if (doc is null) return Results.NotFound();
                    text = await DocumentTextAsync(db, docId, ct);
                }

                var examples = await PoStudio.LoadExamplesAsync(
                    db, profile, id => DocumentTextAsync(db, id, ct), ct);

                var window = req.NumCtxOverride
                    ?? (int.TryParse(config["Extraction:NumCtx"], out var n) && n > 0
                        ? n : PoPromptBuilder.DefaultNumCtx);

                var prompt = PoPromptBuilder.Build(
                    PoStudio.ToPromptProfile(profile, examples), text, window);

                var payload = new
                {
                    buyer = profile?.Name,
                    profileVersion = profile?.Version,
                    model = string.IsNullOrWhiteSpace(profile?.ExtractionModel)
                        ? (config["Extraction:DefaultModel"] ?? config["Ollama:ExtractionModel"])
                        : profile!.ExtractionModel,
                    promptHash = prompt.PromptHash,
                    messages = prompt.Messages.Select(m => new { role = m.Role, content = m.Content }),
                    report = new
                    {
                        windowTokens = prompt.Report.WindowTokens,
                        reservedTokens = prompt.Report.ReservedTokens,
                        documentTokens = prompt.Report.DocumentTokens,
                        systemTokens = prompt.Report.SystemTokens,
                        totalTokens = prompt.Report.TotalTokens,
                        dropped = prompt.Report.Dropped,
                        error = prompt.Report.Error,
                    },
                    exampleRunIds = examples.Select(e => e.RunId),
                    error = prompt.Report.Error,
                };

                // An oversized document is a real failure, not a preview quirk: it
                // answers 422 so the UI shows the size instead of an empty prompt.
                return prompt.Ok ? Results.Ok(payload) : Results.Json(payload, statusCode: 422);
            });

            return app;
        }

        /// <summary>A document's indexed text, in chunk order.</summary>
        private static async Task<string> DocumentTextAsync(AppDbContext db, int documentId, CancellationToken ct)
            => string.Join("\n", await db.EmbeddingChunks
                .Where(c => c.DocumentId == documentId).OrderBy(c => c.Id)
                .Select(c => c.Text).ToListAsync(ct));
    }
}
