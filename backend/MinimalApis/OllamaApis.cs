using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;
using RabbitIC.Api.DTOs;
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


            // List installed Ollama models (proxied so the browser never touches Ollama directly).
            group.MapGet("/models", async(IHttpClientFactory http, CancellationToken ct) =>{

                
                var client = http.CreateClient("ollama");
                
                try
                {
                    var json = await client.GetStringAsync("api/tags", ct);
                    return Results.Content(json, "application/json");
                }
                catch (HttpRequestException)
                {
                    return Results.Problem("Cannot reach Ollama. Is it running?", statusCode: 502);
                }
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
                        Messages = c.Messages
                            .OrderBy(m => m.Id)
                            .Select(m => new { m.Role, m.Content, m.CreatedAt })
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


            // Chat: save the user turn, stream the model reply token-by-token, then save the reply.
            app.MapPost("/chat", async (ChatRequest req, AppDbContext db, IHttpClientFactory http, HttpContext ctx) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                var systemPrompt = Environment.GetEnvironmentVariable("Ollama:SystemPrompt");


                if (user is null)
                {
                    ctx.Response.StatusCode = 401;
                    await ctx.Response.WriteAsync("Unauthorized.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(req.Message) || string.IsNullOrWhiteSpace(req.Model))
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync("model and message are required.");
                    return;
                }

                // Resolve or create the conversation — scoped to this user so you can't post into someone else's.
                Conversation convo;
                if (req.ConversationId is int cid &&
                    await db.Conversations.FirstOrDefaultAsync(c => c.Id == cid && c.UserId == user.Id) is { } existing)
                {
                    convo = existing;
                }
                else
                {
                    convo = new Conversation { UserId = user.Id, Title = Authorization.Truncate(req.Message, 40) };
                    db.Conversations.Add(convo);
                    await db.SaveChangesAsync();
                }

                // Persist the user message.
                db.Messages.Add(new Message { ConversationId = convo.Id, Role = "user", Content = req.Message });
                await db.SaveChangesAsync();

                // Build the full message list for context (this is the "memory").
                var history = await db.Messages
                    .Where(m => m.ConversationId == convo.Id)
                    .OrderBy(m => m.Id)
                    .Select(m => new ChatMsg { Role = m.Role, Content = m.Content })
                    .ToListAsync();

                if (!string.IsNullOrWhiteSpace(systemPrompt))
                    history.Insert(0, new ChatMsg {
                        Role = "system",
                        Content = systemPrompt!
                    });

                var payload = new { model = req.Model, messages = history, stream = true };

                var client = http.CreateClient("ollama");
                using var upstream = new HttpRequestMessage(HttpMethod.Post, "/chat")
                {
                    Content = JsonContent.Create(payload)
                };

                HttpResponseMessage resp;
                try
                {
                    resp = await client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
                    resp.EnsureSuccessStatusCode();
                }
                catch (HttpRequestException ex)
                {
                    ctx.Response.StatusCode = 502;
                    await ctx.Response.WriteAsync("Ollama error: " + ex.Message);
                    return;
                }

                // Tell the client which conversation this is (must be set before the body is written).
                ctx.Response.Headers["X-Conversation-Id"] = convo.Id.ToString();
                ctx.Response.ContentType = "text/plain; charset=utf-8";

                var full = new StringBuilder();
                await using var stream = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted);
                using var reader = new StreamReader(stream);

                string? line;
                while ((line = await reader.ReadLineAsync(ctx.RequestAborted)) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // Ollama streams newline-delimited JSON; pull the content delta out of each line.
                    string? token = null;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.TryGetProperty("message", out var msg) &&
                            msg.TryGetProperty("content", out var content))
                        {
                            token = content.GetString();
                        }
                    }
                    catch (JsonException) { continue; }

                    if (!string.IsNullOrEmpty(token))
                    {
                        full.Append(token);
                        await ctx.Response.WriteAsync(token, ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted); // push each token immediately
                    }
                }

                // Persist the assistant reply once streaming completes.
                db.Messages.Add(new Message { ConversationId = convo.Id, Role = "assistant", Content = full.ToString() });
                await db.SaveChangesAsync();
            });



                        group.MapGet("/version", () => Results.Ok())
                            .WithName("GetOllamaVersion")
                            .WithSummary("Get the version of the Ollama API.")
                            .WithDescription("Returns the version information of the Ollama API.");
                        return app;
                    }
                }
            }
