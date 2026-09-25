using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.MinimalApis
{
    public record NoteCreateInput(string Content, bool IsPinned, string? FolderName);
    public record NoteUpdateInput(string Content, bool IsPinned, string? FolderName, DateTime ExpectedUpdatedAt);

    public static class NoteEndpoints
    {
        // Simple in-memory rate limiter: IP -> (Count, WindowStart)
        private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _rateLimits = new();
        
        public static IEndpointRouteBuilder MapNoteApis(this IEndpointRouteBuilder app)
        {
            var authGroup = app.MapGroup("/api/notes");

            authGroup.MapGet("/", async (
                string? search, string? folder, bool? deleted, 
                HttpContext ctx, AppDbContext db, INoteService svc) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var notes = await svc.ListAsync(user.Id, search, folder, deleted ?? false);
                return Results.Ok(notes);
            });

            authGroup.MapPost("/", async (
                NoteCreateInput input, 
                HttpContext ctx, AppDbContext db, INoteService svc) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var note = await svc.CreateAsync(user.Id, input.Content, input.IsPinned, input.FolderName);
                return Results.Created($"/api/notes/{note.Id}", note);
            });

            authGroup.MapGet("/{id:int}", async (
                int id, 
                HttpContext ctx, AppDbContext db, INoteService svc) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var note = await svc.GetAsync(user.Id, id);
                return note is null ? Results.NotFound() : Results.Ok(note);
            });

            authGroup.MapPut("/{id:int}", async (
                int id, NoteUpdateInput input, 
                HttpContext ctx, AppDbContext db, INoteService svc) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                try
                {
                    var note = await svc.UpdateAsync(user.Id, id, input.Content, input.IsPinned, input.FolderName, input.ExpectedUpdatedAt);
                    return note is null ? Results.NotFound() : Results.Ok(note);
                }
                catch (InvalidOperationException ex) when (ex.Message == "Conflict")
                {
                    return Results.Conflict(new { error = "This note changed elsewhere" });
                }
            });

            authGroup.MapDelete("/{id:int}", async (
                int id, 
                HttpContext ctx, AppDbContext db, INoteService svc) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var success = await svc.SoftDeleteAsync(user.Id, id);
                return success ? Results.NoContent() : Results.NotFound();
            });

            authGroup.MapPost("/{id:int}/restore", async (
                int id, 
                HttpContext ctx, AppDbContext db, INoteService svc) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var success = await svc.RestoreAsync(user.Id, id);
                return success ? Results.Ok() : Results.NotFound();
            });

            authGroup.MapDelete("/{id:int}/permanent", async (
                int id, 
                HttpContext ctx, AppDbContext db, INoteService svc) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var success = await svc.PermanentDeleteAsync(user.Id, id);
                return success ? Results.NoContent() : Results.NotFound();
            });

            authGroup.MapPost("/{id:int}/share", async (
                int id, 
                HttpContext ctx, AppDbContext db, INoteService svc) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var token = await svc.ShareAsync(user.Id, id);
                return token is null ? Results.NotFound() : Results.Ok(new { shareUrl = $"/n/{token}" });
            });

            authGroup.MapDelete("/{id:int}/share", async (
                int id, 
                HttpContext ctx, AppDbContext db, INoteService svc) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var success = await svc.UnshareAsync(user.Id, id);
                return success ? Results.NoContent() : Results.NotFound();
            });

            var publicGroup = app.MapGroup("/api/public/notes");

            publicGroup.MapGet("/{token}", async (
                string token, 
                HttpContext ctx, INoteService svc) =>
            {
                // Rate limit (60 requests / minute)
                var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var now = DateTime.UtcNow;
                var rateLimit = _rateLimits.AddOrUpdate(ip, 
                    _ => (1, now),
                    (_, old) => (now - old.WindowStart).TotalMinutes > 1 ? (1, now) : (old.Count + 1, old.WindowStart));

                if (rateLimit.Count > 60)
                {
                    return Results.StatusCode(429);
                }

                var note = await svc.GetPublicNoteAsync(token);
                if (note is null)
                {
                    return Results.NotFound();
                }

                ctx.Response.Headers["X-Robots-Tag"] = "noindex";
                ctx.Response.Headers["Cache-Control"] = "no-store";
                return Results.Ok(note);
            });

            return app;
        }
    }
}
