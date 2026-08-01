using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Extraction;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.MinimalApis
{
    public record BuyerProfileDto(int Id, string Name, string DetectPattern, string ParserKey,
                                  string ChunkingStrategy, bool IsActive);

    public record BuyerProfileInput(string? Name, string? DetectPattern, string? ParserKey,
                                    string? ChunkingStrategy, bool? IsActive);

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
                                                     p.ChunkingStrategy, p.IsActive))
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
                };
                db.BuyerProfiles.Add(profile);
                await db.SaveChangesAsync();

                return Results.Created($"/api/buyer-profiles/{profile.Id}",
                    new BuyerProfileDto(profile.Id, profile.Name, profile.DetectPattern,
                                        profile.ParserKey, profile.ChunkingStrategy, profile.IsActive));
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

                await db.SaveChangesAsync();
                return Results.Ok(new BuyerProfileDto(profile.Id, profile.Name, profile.DetectPattern,
                                                      profile.ParserKey, profile.ChunkingStrategy, profile.IsActive));
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
    }
}
