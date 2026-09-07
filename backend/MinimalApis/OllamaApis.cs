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


            // Chat: save the user turn, stream the model reply token-by-token, then save the reply.
            app.MapPost("/chat", async (ChatRequest req, AppDbContext db, IHttpClientFactory http, HttpContext ctx,
                                        IConfiguration config, ILoggerFactory logs, ChatService chat) =>
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

                // Per-message web grounding. One search, prepended as a system message.
                // If search is unconfigured or fails we still answer — just without it.
                var webSearchUsed = false;
                string? searchNotice = null;
                if (req.WebSearch)
                {
                    if (SearchApis.IsConfigured(config))
                    {
                        var context = await WebSearchContext.BuildAsync(
                            http.CreateClient("ollama-cloud"),
                            req.Message,
                            logs.CreateLogger("ChatWebSearch"),
                            ctx.RequestAborted);

                        if (context is not null)
                        {
                            history.Insert(0, new ChatMsg { Role = "system", Content = context });
                            webSearchUsed = true;
                        }
                        else
                        {
                            searchNotice = "_Web search returned no usable results — answering without it._\n\n";
                        }
                    }
                    else
                    {
                        searchNotice = "_Web search is not configured on this server — answering without it._\n\n";
                    }
                }

                // Route to the requested source through the registry. The endpoint
                // knows only the key it was given — never a concrete source type.
                var turns = history.Select(m => new ChatTurn(m.Role, m.Content)).ToList();

                IAsyncEnumerable<string> tokens;
                try
                {
                    tokens = chat.StreamAsync(req.Source, new ChatArgs(req.Model, turns), ctx.RequestAborted);
                }
                catch (ChatSourceException ex)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync(ex.Message);
                    return;
                }

                // Tell the client which conversation this is (must be set before the body is written).
                ctx.Response.Headers["X-Conversation-Id"] = convo.Id.ToString();
                ctx.Response.ContentType = "text/plain; charset=utf-8";

                var full = new StringBuilder();

                // Tell the user up-front that the reply is ungrounded despite the toggle.
                if (searchNotice is not null)
                {
                    full.Append(searchNotice);
                    await ctx.Response.WriteAsync(searchNotice, ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }

                // The source yields content deltas; the transport detail (NDJSON) is
                // its business, not this endpoint's.
                try
                {
                    await foreach (var token in tokens.WithCancellation(ctx.RequestAborted))
                    {
                        if (string.IsNullOrEmpty(token)) continue;

                        full.Append(token);
                        await ctx.Response.WriteAsync(token, ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted); // push each token immediately
                    }
                }
                catch (ChatSourceException ex)
                {
                    // Headers are already sent, so the failure is reported in-band.
                    var notice = $"\n\n_{ex.Message}_";
                    full.Append(notice);
                    await ctx.Response.WriteAsync(notice, ctx.RequestAborted);
                }

                // Persist the assistant reply once streaming completes.
                db.Messages.Add(new Message
                {
                    ConversationId = convo.Id,
                    Role = "assistant",
                    Content = full.ToString(),
                    WebSearchUsed = webSearchUsed,
                });
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
