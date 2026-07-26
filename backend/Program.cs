using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Middleware;
using RabbitIC.Api.Models;

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

// CORS so a Vue app on another origin can call us (the Vite dev proxy also covers this).
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173", "http://192.168.61.41:4200",
                  "https://weavo-go.vercel.app", "http://127.0.0.1:5173",
                  "http://10.10.60.156:5173/", "http://192.168.61.41:5173/",
                  "http://10.10.60.156:4200", "http://192.168.61.41:4200", "http://localhost:4200")
     .AllowAnyHeader()
     .AllowAnyMethod()
     .WithExposedHeaders("X-Conversation-Id")));





var app = builder.Build();

app.ApplicationPublicApis();
app.ApplicationOllamaApis();


app.UseCors();

// Create the SQLite file + tables on first run, then bring an older db up to the new schema.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();   // creates everything fresh if the file is new
    SchemaUpgrade.Run(db);         // one-time, idempotent upgrade for older db files
}


app.Run();
