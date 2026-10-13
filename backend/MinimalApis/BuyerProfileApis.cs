using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Extraction;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.MinimalApis
{
    public record BuyerProfileDto(int Id, string Name, string DetectPattern, string ParserKey,
                                  string ChunkingStrategy, bool IsActive,
                                  string ExtractionModel, string Rules, string Glossary,
                                  string ExampleRunIds, int Version);

    public record BuyerProfileInput(string? Name, string? DetectPattern, string? ParserKey,
                                    string? ChunkingStrategy, bool? IsActive,
                                    string? ExtractionModel, string? Rules, string? Glossary,
                                    string? ExampleRunIds);

    public static class BuyerProfileApis
    {
        public static IEndpointRouteBuilder MapBuyerProfileApis(this IEndpointRouteBuilder app)
        {
            // Profiles are shared team configuration, but still require a session.
            app.MapGet("/buyer-profiles", async (HttpContext ctx, AppDbContext db) =>
            {
                if (await Authorization.ResolveUser(ctx, db) is null) return Results.Unauthorized();

                return Results.Ok(await db.BuyerProfiles
                    .OrderByDescending(p => p.IsActive).ThenBy(p => p.Name)
                    .Select(p => new BuyerProfileDto(p.Id, p.Name, p.DetectPattern, p.ParserKey,
                                                     p.ChunkingStrategy, p.IsActive,
                                                     p.ExtractionModel, p.Rules, p.Glossary,
                                                     p.ExampleRunIds, p.Version))
                    .ToListAsync());
            });

            // Registered parser keys — the UI offers real choices instead of free text.
            app.MapGet("/buyer-profiles/parsers", async (
                HttpContext ctx, AppDbContext db, IEnumerable<IPoParser> parsers) =>
            {
                if (await Authorization.ResolveUser(ctx, db) is null) return Results.Unauthorized();

                return Results.Ok(parsers
                    .Select(p => new { key = p.Key, name = p.DisplayName })
                    .Append(new { key = "GenericLlm", name = "Generic (model fallback)" }));
            });

            app.MapPost("/buyer-profiles", async (
                BuyerProfileInput input, HttpContext ctx, AppDbContext db) =>
            {
                if (await Authorization.ResolveUser(ctx, db) is null) return Results.Unauthorized();
                if (string.IsNullOrWhiteSpace(input.Name))
                    return Results.BadRequest(new { error = "name is required." });
                if (string.IsNullOrWhiteSpace(input.DetectPattern))
                    return Results.BadRequest(new { error = "detectPattern is required (pipe-separated markers)." });

                var name = input.Name.Trim();
                if (await db.BuyerProfiles.AnyAsync(p => p.Name == name))
                    return Results.Conflict(new { error = $"A profile named '{name}' already exists." });

                var profile = new BuyerProfile
                {
                    Name = name,
                    DetectPattern = input.DetectPattern.Trim(),
                    ParserKey = string.IsNullOrWhiteSpace(input.ParserKey) ? "GenericLlm" : input.ParserKey.Trim(),
                    ChunkingStrategy = input.ChunkingStrategy ?? "none",
                    IsActive = input.IsActive ?? true,
                    ExtractionModel = (input.ExtractionModel ?? "").Trim(),
                    Rules = input.Rules ?? "",
                    Glossary = input.Glossary ?? "",
                };

                // Worked examples are checked before the row exists, so a refusal
                // leaves nothing half-created.
                var (exampleIds, refusal) = await ResolveExamplesAsync(db, input.ExampleRunIds, default);
                if (refusal is not null) return Results.BadRequest(new { error = refusal });
                profile.ExampleRunIds = PoStudio.FormatRunIds(exampleIds);

                db.BuyerProfiles.Add(profile);
                await db.SaveChangesAsync();

                return Results.Created($"/api/buyer-profiles/{profile.Id}", Dto(profile));
            });

            app.MapPut("/buyer-profiles/{id:int}", async (
                int id, BuyerProfileInput input, HttpContext ctx, AppDbContext db) =>
            {
                if (await Authorization.ResolveUser(ctx, db) is null) return Results.Unauthorized();

                var profile = await db.BuyerProfiles.FindAsync(id);
                if (profile is null) return Results.NotFound();

                if (!string.IsNullOrWhiteSpace(input.Name)) profile.Name = input.Name.Trim();
                if (!string.IsNullOrWhiteSpace(input.DetectPattern)) profile.DetectPattern = input.DetectPattern.Trim();
                if (!string.IsNullOrWhiteSpace(input.ParserKey)) profile.ParserKey = input.ParserKey.Trim();
                if (input.ChunkingStrategy is not null) profile.ChunkingStrategy = input.ChunkingStrategy;
                if (input.IsActive is bool active) profile.IsActive = active;
                if (input.ExtractionModel is not null) profile.ExtractionModel = input.ExtractionModel.Trim();
                if (input.Rules is not null) profile.Rules = input.Rules;
                if (input.Glossary is not null) profile.Glossary = input.Glossary;

                if (input.ExampleRunIds is not null)
                {
                    var (exampleIds, refusal) = await ResolveExamplesAsync(db, input.ExampleRunIds, default);
                    if (refusal is not null) return Results.BadRequest(new { error = refusal });
                    profile.ExampleRunIds = PoStudio.FormatRunIds(exampleIds);
                }

                // Every save is a new revision: a run records the version that
                // produced it, so the accuracy trend can show whether an edit helped.
                profile.Version++;

                await db.SaveChangesAsync();
                return Results.Ok(Dto(profile));
            });

            app.MapDelete("/buyer-profiles/{id:int}", async (int id, HttpContext ctx, AppDbContext db) =>
            {
                if (await Authorization.ResolveUser(ctx, db) is null) return Results.Unauthorized();

                var profile = await db.BuyerProfiles.FindAsync(id);
                if (profile is null) return Results.NotFound();

                db.BuyerProfiles.Remove(profile);
                await db.SaveChangesAsync();
                return Results.NoContent();
            });

            // Detection dry-run against an uploaded document — for tuning a
            // DetectPattern before trusting it to route extractions.
            app.MapPost("/buyer-profiles/detect/{documentId:int}", async (
                int documentId, HttpContext ctx, AppDbContext db,
                ExtractionDispatcher dispatcher, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var doc = await db.Documents
                    .FirstOrDefaultAsync(d => d.Id == documentId && d.OwnerUserId == user.Id, ct);
                if (doc is null) return Results.NotFound();

                async Task<string> ChunkText() => string.Join("\n", await db.EmbeddingChunks
                    .Where(c => c.DocumentId == documentId).OrderBy(c => c.Id)
                    .Select(c => c.Text).ToListAsync(ct));

                var probe = await dispatcher.ProbeAsync(doc, ChunkText, ct);
                return Results.Ok(new
                {
                    buyer = probe.BuyerName,
                    buyerProfileId = probe.ProfileId,
                    parserKey = probe.ParserKey,
                    detectConfidence = Math.Round(probe.Confidence, 2),
                    parserRegistered = probe.ParserRegistered,
                });
            });

            return app;
        }

        private static BuyerProfileDto Dto(BuyerProfile p) =>
            new(p.Id, p.Name, p.DetectPattern, p.ParserKey, p.ChunkingStrategy, p.IsActive,
                p.ExtractionModel, p.Rules, p.Glossary, p.ExampleRunIds, p.Version);

        /// <summary>
        /// Validates the requested worked examples against the §3.3 and §3.6 rules
        /// (confirmed, not held out, at most two) and returns them in order, or the
        /// reason the first offending one was refused.
        /// </summary>
        private static async Task<(List<int> Ids, string? Refusal)> ResolveExamplesAsync(
            AppDbContext db, string? csv, CancellationToken ct)
        {
            var ids = PoStudio.ParseRunIds(csv);
            if (ids.Count > PoStudio.MaxExamples)
                return (ids, $"At most {PoStudio.MaxExamples} worked examples fit a prompt; {ids.Count} were given.");

            foreach (var id in ids)
            {
                var refusal = await PoStudio.ExampleRefusalAsync(db, id, ct);
                if (refusal is not null) return (ids, refusal);
            }
            return (ids, null);
        }
    }
}
