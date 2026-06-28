using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;

var builder = WebApplication.CreateBuilder(args);

// ---- Config ----
var ollamaUrl = builder.Configuration["Ollama:Url"] ?? "http://localhost:11434";
var systemPrompt = builder.Configuration["Ollama:SystemPrompt"]; // optional persona

// ---- Services ----
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=rabbitic.db"));

builder.Services.AddHttpClient("ollama", c =>
{
    c.BaseAddress = new Uri(ollamaUrl);
    c.Timeout = TimeSpan.FromMinutes(10); // local models can be slow on first load
});

// CORS so a Vue app on another origin can call us (the Vite dev proxy also covers this).
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
     .AllowAnyHeader()
     .AllowAnyMethod()
     .WithExposedHeaders("X-Conversation-Id")));

var app = builder.Build();
app.UseCors();

// Create the SQLite file + tables on first run (no migrations needed for this).
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();

// ===================== Endpoints =====================

// List installed Ollama models (proxied so the browser never touches Ollama directly).
app.MapGet("/api/models", async (IHttpClientFactory http, CancellationToken ct) =>
{
    var client = http.CreateClient("ollama");
    try
    {
        var json = await client.GetStringAsync("/api/tags", ct);
        return Results.Content(json, "application/json");
    }
    catch (HttpRequestException)
    {
        return Results.Problem("Cannot reach Ollama. Is it running?", statusCode: 502);
    }
});

// List conversations (newest first).
app.MapGet("/api/conversations", async (AppDbContext db) =>
    await db.Conversations
        .OrderByDescending(c => c.CreatedAt)
        .Select(c => new { c.Id, c.Title, c.CreatedAt })
        .ToListAsync());

// Get one conversation with its messages.
app.MapGet("/api/conversations/{id:int}", async (int id, AppDbContext db) =>
{
    var convo = await db.Conversations
        .Where(c => c.Id == id)
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

// Delete a conversation.
app.MapDelete("/api/conversations/{id:int}", async (int id, AppDbContext db) =>
{
    var convo = await db.Conversations.FindAsync(id);
    if (convo is null) return Results.NotFound();
    db.Conversations.Remove(convo);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// Chat: save the user turn, stream the model reply token-by-token, then save the reply.
app.MapPost("/api/chat", async (ChatRequest req, AppDbContext db, IHttpClientFactory http, HttpContext ctx) =>
{
    if (string.IsNullOrWhiteSpace(req.Message) || string.IsNullOrWhiteSpace(req.Model))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("model and message are required.");
        return;
    }

    // Resolve or create the conversation.
    Conversation convo;
    if (req.ConversationId is int cid && await db.Conversations.FindAsync(cid) is { } existing)
    {
        convo = existing;
    }
    else
    {
        convo = new Conversation { Title = Truncate(req.Message, 40) };
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
        .Select(m => new ChatMsg(m.Role, m.Content))
        .ToListAsync();

    if (!string.IsNullOrWhiteSpace(systemPrompt))
        history.Insert(0, new ChatMsg("system", systemPrompt!));

    var payload = new { model = req.Model, messages = history, stream = true };

    var client = http.CreateClient("ollama");
    using var upstream = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
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

app.Run();

static string Truncate(string s, int n) =>
    s.Length <= n ? s : s[..n].TrimEnd() + "…";

// ---- DTOs ----
record ChatRequest(int? ConversationId, string Model, string Message);
record ChatMsg(string role, string content);
