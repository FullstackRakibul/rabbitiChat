using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;
using RabbitIC.Api.DTOs;
using RabbitIC.Api.ModelSources;
using RabbitIC.Api.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RabbitIC.Api.MinimalApis
{
    public static class OllamaApis
    {

        public static IEndpointRouteBuilder MapOllamaApis(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/").WithTags("Ollama APIs");


            // Merged model list: every registered source, each entry tagged with its
            // source and a metered flag. Routed through ChatService so this endpoint
            // never names a concrete source.
            group.MapGet("/models", async (ChatService chat, CancellationToken ct) =>
            {
                var models = await chat.ListAllModelsAsync(ct);

                return Results.Ok(models
                    .Select(m => new ModelDto(m.Name, m.Source, m.ContextLength, m.Capabilities, m.Metered))
                    .ToList());
            });


            // Login or auto-register. New username => create account; existing => verify secret.
            app.MapPost("/auth/login", async (LoginRequest req, AppDbContext db) =>
            {
                if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Secret))
                    return Results.BadRequest(new { error = "username and secret are required." });

                var username = req.Username.Trim();
                var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
                bool created = false;

                if (user is null)
                {
                    var (hash, salt) = Authorization.HashSecret(req.Secret);
                    user = new User { Username = username, SecretHash = hash, SecretSalt = salt, Token = Authorization.NewToken() };
                    db.Users.Add(user);
                    await db.SaveChangesAsync();
                    created = true;
                }
                else
                {
                    var (hash, _) = Authorization.HashSecret(req.Secret, Convert.FromBase64String(user.SecretSalt));
                    if (!CryptographicOperations.FixedTimeEquals(
                            Convert.FromBase64String(hash), Convert.FromBase64String(user.SecretHash)))
                        return Results.Json(new { error = "Wrong secret for that username." }, statusCode: 401);

                    user.Token = Authorization.NewToken(); // fresh token each login
                    await db.SaveChangesAsync();
                }

                return Results.Ok(new { username = user.Username, token = user.Token, created });
            });




            // List the current user's conversations (newest first).
            app.MapGet("/conversations", async (HttpContext ctx, AppDbContext db) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return Results.Ok(await db.Conversations
                    .Where(c => c.UserId == user.Id)
                    .OrderByDescending(c => c.CreatedAt)
                    .Select(c => new { c.Id, c.Title, c.CreatedAt })
                    .ToListAsync());
            });


            
            // Get one of the current user's conversations with its messages.
            app.MapGet("/conversations/{id:int}", async (int id, HttpContext ctx, AppDbContext db) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var convo = await db.Conversations
                    .Where(c => c.Id == id && c.UserId == user.Id)
                    .Select(c => new
                    {
                        c.Id,
                        c.Title,
                        c.CreatedAt,
                        c.ActiveSourceIds,
                        Messages = c.Messages
                            .OrderBy(m => m.Id)
                            .Select(m => new { m.Role, m.Content, m.WebSearchUsed, m.CreatedAt })
                    })
                    .FirstOrDefaultAsync();

                return convo is null ? Results.NotFound() : Results.Ok(convo);
            });


            // Delete one of the current user's conversations.
            app.MapDelete("/conversations/{id:int}", async (int id, HttpContext ctx, AppDbContext db) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var convo = await db.Conversations.FirstOrDefaultAsync(c => c.Id == id && c.UserId == user.Id);
                if (convo is null) return Results.NotFound();
                db.Conversations.Remove(convo);
                await db.SaveChangesAsync();
                return Results.NoContent();
            });


            // Chat: save the user turn, stream the model reply, then save the reply.
            // The orchestration (context assembly, tool calls, persistence) lives in
            // ChatEndpoint so this file stays a routing table.
            app.MapPost("/chat", ChatEndpoint.HandleAsync);


            group.MapGet("/version", () => Results.Ok())
                .WithName("GetOllamaVersion")
                .WithSummary("Get the version of the Ollama API.")
                .WithDescription("Returns the version information of the Ollama API.");
            return app;
        }
    }
}
