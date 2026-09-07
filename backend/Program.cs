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



// ---- Services ----
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=rabbitic.db"));


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

builder.Services.AddSingleton<RabbitIC.Api.ModelSources.IModelSourceRegistry,
                              RabbitIC.Api.ModelSources.ModelSourceRegistry>();
builder.Services.AddSingleton<RabbitIC.Api.Services.ChatService>();

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
app.ApplicationTodoRabbitApis();


app.UseCors();

// Create the SQLite file + tables on first run, then bring an older db up to the new schema.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();   // creates everything fresh if the file is new
    SchemaUpgrade.Run(db);         // one-time, idempotent upgrade for older db files

    // Warm the vector index from SQLite so retrieval works from the first request.
    var vectorIndex = scope.ServiceProvider.GetRequiredService<RabbitIC.Api.Retrieval.VectorIndex>();
    vectorIndex.Load(db.EmbeddingChunks.AsNoTracking());
    app.Logger.LogInformation("VectorIndex loaded with {Count} chunks.", vectorIndex.Count);
}


app.Run();
