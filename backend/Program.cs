using System.Net.Http.Json;
using System.Security.Cryptography;
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
    p.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173", "http://10.10.60.156:5173/", "http://192.168.61.41:5173/", "http://10.10.60.156:4200")
     .AllowAnyHeader()
     .AllowAnyMethod()
     .WithExposedHeaders("X-Conversation-Id")));

var app = builder.Build();
app.UseCors();

// Create the SQLite file + tables on first run, then bring an older db up to the new schema.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();   // creates everything fresh if the file is new
    SchemaUpgrade.Run(db);         // one-time, idempotent upgrade for older db files
}

// ===================== Auth helpers =====================

// Resolve the user from the Authorization: Bearer <token> header. Null if missing/invalid.
static async Task<User?> ResolveUser(HttpContext ctx, AppDbContext db)
{
    var header = ctx.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
    var token = header["Bearer ".Length..].Trim();
    if (string.IsNullOrEmpty(token)) return null;
    return await db.Users.FirstOrDefaultAsync(u => u.Token == token);
}

static (string hash, string salt) HashSecret(string secret, byte[]? existingSalt = null)
{
    var salt = existingSalt ?? RandomNumberGenerator.GetBytes(16);
    var hash = Rfc2898DeriveBytes.Pbkdf2(
        Encoding.UTF8.GetBytes(secret), salt, 100_000, HashAlgorithmName.SHA256, 32);
    return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
}

static string NewToken() =>
    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

// ===================== Endpoints =====================

// Login or auto-register. New username => create account; existing => verify secret.
app.MapPost("/api/auth/login", async (LoginRequest req, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Secret))
        return Results.BadRequest(new { error = "username and secret are required." });

    var username = req.Username.Trim();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
    bool created = false;

    if (user is null)
    {
        var (hash, salt) = HashSecret(req.Secret);
        user = new User { Username = username, SecretHash = hash, SecretSalt = salt, Token = NewToken() };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        created = true;
    }
    else
    {
        var (hash, _) = HashSecret(req.Secret, Convert.FromBase64String(user.SecretSalt));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(hash), Convert.FromBase64String(user.SecretHash)))
            return Results.Json(new { error = "Wrong secret for that username." }, statusCode: 401);

        user.Token = NewToken(); // fresh token each login
        await db.SaveChangesAsync();
    }

    return Results.Ok(new { username = user.Username, token = user.Token, created });
});

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

// List the current user's conversations (newest first).
app.MapGet("/api/conversations", async (HttpContext ctx, AppDbContext db) =>
{
    var user = await ResolveUser(ctx, db);
    if (user is null) return Results.Unauthorized();

    return Results.Ok(await db.Conversations
        .Where(c => c.UserId == user.Id)
        .OrderByDescending(c => c.CreatedAt)
        .Select(c => new { c.Id, c.Title, c.CreatedAt })
        .ToListAsync());
});

// Get one of the current user's conversations with its messages.
app.MapGet("/api/conversations/{id:int}", async (int id, HttpContext ctx, AppDbContext db) =>
{
    var user = await ResolveUser(ctx, db);
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
app.MapDelete("/api/conversations/{id:int}", async (int id, HttpContext ctx, AppDbContext db) =>
{
    var user = await ResolveUser(ctx, db);
    if (user is null) return Results.Unauthorized();

    var convo = await db.Conversations.FirstOrDefaultAsync(c => c.Id == id && c.UserId == user.Id);
    if (convo is null) return Results.NotFound();
    db.Conversations.Remove(convo);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// Chat: save the user turn, stream the model reply token-by-token, then save the reply.
app.MapPost("/api/chat", async (ChatRequest req, AppDbContext db, IHttpClientFactory http, HttpContext ctx) =>
{
    var user = await ResolveUser(ctx, db);
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
        convo = new Conversation { UserId = user.Id, Title = Truncate(req.Message, 40) };
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
record LoginRequest(string Username, string Secret);
record ChatRequest(int? ConversationId, string Model, string Message);
record ChatMsg(string role, string content);
