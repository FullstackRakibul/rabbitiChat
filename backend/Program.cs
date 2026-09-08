using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Middleware;
using RabbitIC.Api.Models;
using RabbitIC.Api.Retrieval;

var builder = WebApplication.CreateBuilder(args);


// ---- Config ----
var ollamaUrl = builder.Configuration["Ollama:Url"] ?? "http://localhost:11434";
//var ollamaUrl = "https://ollama.com";
//var systemPrompt = builder.Configuration["Ollama:SystemPrompt"]; // optional persona



// ---- SQLite path ----
// The connection string holds a relative path, which SQLite resolves against the
// *current working directory* — that is not the app folder under IIS, a Windows
// service, or `dotnet run` from elsewhere, and the open then fails with
// "SQLite Error 14: unable to open database file". Anchor it to the deployed
// binaries instead and make sure the folder exists before EF opens the file.
var sqliteBuilder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(
    builder.Configuration.GetConnectionString("Default") ?? "Data Source=Data/rabbitic.db");

if (!Path.IsPathRooted(sqliteBuilder.DataSource))
    sqliteBuilder.DataSource = Path.Combine(AppContext.BaseDirectory, sqliteBuilder.DataSource);

var dbDirectory = Path.GetDirectoryName(sqliteBuilder.DataSource);
if (!string.IsNullOrEmpty(dbDirectory))
    Directory.CreateDirectory(dbDirectory);   // no-op when it already exists

var sqliteConnectionString = sqliteBuilder.ToString();

// ---- Services ----
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(sqliteConnectionString));


builder.Services.AddHttpClient("ollama", c =>
{
    c.BaseAddress = new Uri(ollamaUrl);
    c.Timeout = TimeSpan.FromMinutes(10); // local models can be slow on first load
});

// Ollama's hosted web search. The key stays server-side — the browser calls our
// /api/search proxy instead. A missing key is NOT fatal here: the endpoints answer
// 503 at request time so the rest of the app still boots.
var ollamaCloudKey = builder.Configuration["OllamaCloud:ApiKey"];
// URL convention for every Ollama client, local and cloud:
//   BaseAddress = origin ONLY ("https://ollama.com"), request paths carry "/api/…".
//
// A rooted request path ("/api/web_search") replaces the whole path of the base
// address, so a BaseAddress of "https://ollama.com/api" silently discarded its
// own "/api" segment and produced https://ollama.com/api/web_search only by
// coincidence — any base path would have been thrown away the same way. Keeping
// the base as a bare origin makes the resolved URL obvious at each call site.
builder.Services.AddHttpClient("ollama-cloud", c =>
{
    c.BaseAddress = new Uri(
        builder.Configuration["OllamaCloud:BaseUrl"]?.TrimEnd('/') ?? "https://ollama.com");
    c.Timeout = TimeSpan.FromSeconds(30);
    if (!string.IsNullOrWhiteSpace(ollamaCloudKey))
        c.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ollamaCloudKey);
});

// ---- Swagger (API explorer) ----
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "RabbitIC API",
        Version = "v1",
        Description = "Chat + document ingestion (RAG) API. Authorize with the bearer token from /api/auth/login.",
    });
    // Bearer token auth so protected endpoints are testable from the UI.
    o.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Paste the token returned by /api/auth/login.",
    });
    o.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer",
                },
            },
            Array.Empty<string>()
        },
    });
});

// ---- Retrieval (RAG) ----
builder.Services.AddSingleton<VectorIndex>();       // in-memory index, lives for the app lifetime
builder.Services.AddScoped<EmbeddingService>();     // Ollama /api/embeddings client

// ---- PO extraction ----
// Deterministic parsers register here as IPoParser implementations; the
// dispatcher routes to them by BuyerProfile.ParserKey and falls back to the LLM.
builder.Services.AddScoped<RabbitIC.Api.Extraction.LlmPoFallback>();
builder.Services.AddScoped<RabbitIC.Api.Extraction.ExtractionDispatcher>();

// ---- Model sources ----
// Each source is registered against the shared IChatModelSource abstraction, so
// the registry (and therefore ChatService and the endpoints) discovers them
// without naming any concrete type. A third provider is one AddSingleton line.
builder.Services.Configure<RabbitIC.Api.ModelSources.OllamaCloudOptions>(
    builder.Configuration.GetSection(RabbitIC.Api.ModelSources.OllamaCloudOptions.SectionName));

builder.Services.AddSingleton<RabbitIC.Api.ModelSources.IChatModelSource,
                              RabbitIC.Api.ModelSources.LocalOllamaSource>();
builder.Services.AddSingleton<RabbitIC.Api.ModelSources.IChatModelSource,
                              RabbitIC.Api.ModelSources.CloudOllamaSource>();

// Opt-in diagnostic source. Proof of the Open/Closed claim: this one line and a
// new file were the entire cost of adding a third provider.
if (builder.Configuration.GetValue<bool>("ModelSources:EnableEcho"))
    builder.Services.AddSingleton<RabbitIC.Api.ModelSources.IChatModelSource,
                                  RabbitIC.Api.ModelSources.EchoModelSource>();

// ---- OpenAI-compatible providers (OpenRouter, Groq, …) ----
// One adapter class serves them all; each configured provider becomes its own
// registry entry with its own named HttpClient. A provider without a key is
// skipped silently — the same "unconfigured is not an error" rule the web-search
// proxy uses — so a shared appsettings.json can list providers this machine
// hasn't got credentials for.
var openAiProviders = builder.Configuration
    .GetSection(RabbitIC.Api.ModelSources.OpenAiCompatibleOptions.SectionName)
    .Get<RabbitIC.Api.ModelSources.OpenAiCompatibleOptions>()
    ?? new RabbitIC.Api.ModelSources.OpenAiCompatibleOptions();

foreach (var provider in openAiProviders.Providers)
{
    if (!provider.IsConfigured) continue;

    builder.Services.AddHttpClient(provider.HttpClientName, c =>
    {
        // Trailing slash matters: the stream appends the relative path
        // "chat/completions", and without it the last URL segment (".../v1")
        // would be replaced instead of extended.
        c.BaseAddress = new Uri(provider.BaseUrl.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromSeconds(60);
        c.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);

        foreach (var (name, value) in provider.ExtraHeaders)
        {
            if (!string.IsNullOrWhiteSpace(name))
                c.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }
    });

    // Captured per iteration so each source keeps its own provider config.
    var captured = provider;
    builder.Services.AddSingleton<RabbitIC.Api.ModelSources.IChatModelSource>(sp =>
        new RabbitIC.Api.ModelSources.OpenAiCompatibleSource(
            captured,
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILogger<RabbitIC.Api.ModelSources.OpenAiCompatibleSource>>()));
}

builder.Services.AddSingleton<RabbitIC.Api.ModelSources.IModelSourceRegistry,
                              RabbitIC.Api.ModelSources.ModelSourceRegistry>();
builder.Services.AddSingleton<RabbitIC.Api.Services.ChatService>();

// ---- Context sources ----
// Connected sources and buyer profiles are one mechanism behind one interface,
// so chat and workspace cannot drift apart in how they assemble context.
builder.Services.AddScoped<RabbitIC.Api.Services.IContextSourceService,
                           RabbitIC.Api.Services.ContextSourceService>();

// Shared by the chat tool-call path and the /api/search proxy path — one place
// that knows how to reach the upstream search API.
builder.Services.AddScoped<RabbitIC.Api.Services.WebSearchRunner>();

// Search history — the dedupe rule and the never-hard-delete guarantee live
// behind this interface, so no endpoint can work around either.
builder.Services.AddScoped<RabbitIC.Api.Services.ISearchHistoryService,
                           RabbitIC.Api.Services.SearchHistoryService>();

// ---- todorabbit (Kanban boards) ----
builder.Services.AddScoped<RabbitIC.Api.Repositories.ITodoRabbitRepository,
                           RabbitIC.Api.Repositories.TodoRabbitRepository>();
builder.Services.AddScoped<RabbitIC.Api.Services.TodoRabbitService>();

// CORS so a Vue app on another origin can call us (the Vite dev proxy also covers this).
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173", "http://192.168.61.41:4200",
                  "https://weavo-go.vercel.app", "http://127.0.0.1:5173",
                  "http://10.10.60.156:5173/", "http://192.168.61.41:5173/",
                  "http://10.10.60.156:4200", "http://192.168.61.41:4200", 
                  "http://localhost:4200")
     .AllowAnyHeader()
     .AllowAnyMethod()
     .WithExposedHeaders("X-Conversation-Id")));





var app = builder.Build();

// Swagger UI at /swagger — enabled in all environments (this is an internal tool).
app.UseSwagger();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/swagger/v1/swagger.json", "RabbitIC API v1");
});

app.ApplicationPublicApis();
app.ApplicationOllamaApis();
app.ApplicationDocumentApis();
app.ApplicationBuyerProfileApis();
app.ApplicationSearchApis();
app.ApplicationContextSourceApis();
app.ApplicationTodoRabbitApis();


app.UseCors();

// Create the SQLite file + tables on first run, then bring an older db up to the new schema.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    app.Logger.LogInformation("SQLite database: {Path}", sqliteBuilder.DataSource);
    db.Database.EnsureCreated();   // creates everything fresh if the file is new
    SchemaUpgrade.Run(db);         // one-time, idempotent upgrade for older db files

    // Warm the vector index from SQLite so retrieval works from the first request.
    var vectorIndex = scope.ServiceProvider.GetRequiredService<RabbitIC.Api.Retrieval.VectorIndex>();
    vectorIndex.Load(db.EmbeddingChunks.AsNoTracking());
    app.Logger.LogInformation("VectorIndex loaded with {Count} chunks.", vectorIndex.Count);
}


app.Run();
