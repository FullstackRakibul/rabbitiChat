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
