using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Middleware;
using RabbitIC.Api.MinimalApis;
using RabbitIC.Api.Models;
using RabbitIC.Api.Observability;
using RabbitIC.Api.Rbac;
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


//var connectionString = builder.Configuration.GetConnectionString("Default")
//                      ?? "Data Source=Data/rabbitic.db";



if (!Path.IsPathRooted(sqliteBuilder.DataSource))
	sqliteBuilder.DataSource = Path.Combine(AppContext.BaseDirectory, sqliteBuilder.DataSource);

var dbDirectory = Path.GetDirectoryName(sqliteBuilder.DataSource);
if (!string.IsNullOrEmpty(dbDirectory))
	Directory.CreateDirectory(dbDirectory);   // no-op when it already exists

var sqliteConnectionString = sqliteBuilder.ToString();

//var sqliteConnectionString = connectionString.ToString();


//builder.Services.AddDbContext<AppDbContext>(options =>
//{
//    // Check if it's pointing to your remote Turso HTTP pipeline format
//    if (connectionString.StartsWith("https://") && connectionString.Contains("/v2/pipeline"))
//    {
//        options.UseLibSql(connectionString);
//    }
//    else
//    {
//        // Fallback for standard local SQLite files
//        options.UseSqlite(connectionString);
//    }
//});


// ---- Observability — must be first so the interceptor can be added to EF ----
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IRedactor, Redactor>();
builder.Services.AddSingleton<ILogQueue, BoundedLogQueue>();
builder.Services.AddSingleton<LogStreamBroadcaster>();
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.AddScoped<ActivityLogQueryService>();
builder.Services.AddHostedService<LogWriterBackgroundService>();

// ---- Services ----
builder.Services.AddDbContext<AppDbContext>((sp, o) =>
{
    o.UseSqlite(sqliteConnectionString);
    // Wire the EF audit interceptor so every SaveChanges produces audit rows.
    var interceptor = sp.GetRequiredService<AuditInterceptor>();
    o.AddInterceptors(interceptor);
});


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

// ---- Angular component splitter ----
// The reasoner depends on ChatService only, so the splitter works with every
// registered model source and names none of them. Both are stateless per call.
builder.Services.AddSingleton<RabbitIC.Api.ComponentSplitter.Reasoning.SplitReasoner>();
builder.Services.AddSingleton<RabbitIC.Api.ComponentSplitter.SplitterService>();

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

// ---- RBAC (roles + per-tab permissions) ----
// Layered on the existing username+secret auth: the service adds who-may-do-what
// on top of ResolveUser, which still answers who-is-this.
builder.Services.AddScoped<RabbitIC.Api.Repositories.IRbacRepository,
							 RabbitIC.Api.Repositories.RbacRepository>();
builder.Services.AddScoped<RabbitIC.Api.Services.RbacService>();

// ---- IELTS ----
// The mistake spine is registered first because the Reading engine depends on
// it: every wrong answer a sitting produces is logged through IMistakeService.
builder.Services.AddScoped<RabbitIC.Api.Services.IMistakeService,
							 RabbitIC.Api.Services.MistakeService>();
builder.Services.AddScoped<RabbitIC.Api.Services.IReadingService,
							 RabbitIC.Api.Services.ReadingService>();

// ---- todorabbit (Kanban boards) ----
builder.Services.AddScoped<RabbitIC.Api.Repositories.ITodoRabbitRepository,
							 RabbitIC.Api.Repositories.TodoRabbitRepository>();
builder.Services.AddScoped<RabbitIC.Api.Services.TodoRabbitService>();

// ---- Notes ----
builder.Services.AddScoped<RabbitIC.Api.Services.INoteService,
							 RabbitIC.Api.Services.NoteService>();

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

// ---- Single-origin hosting (Docker) ----
// The published image copies the built Vue app into wwwroot, so the SPA and the
// API answer on ONE port and the browser is same-origin — no CORS, no second
// host, and VITE_API_BASE_URL can stay at its "/api" default.
//
// When wwwroot is absent (running `dotnet run` against the Vite dev server)
// these are no-ops, so development is unchanged.
app.UseDefaultFiles();
app.UseStaticFiles();

// RBAC enforcement — runs ahead of every endpoint below, so a user without a
// tab is refused there regardless of what the UI chose to render.
app.UseCorrelation();         // X-Correlation-Id on every request
app.UseRequestLogging();      // ActivityLog entry after response completes
app.UseTabAccess();

app.ApplicationPublicApis();
app.ApplicationOllamaApis();
app.ApplicationDocumentApis();
app.ApplicationBuyerProfileApis();
app.ApplicationSearchApis();
app.ApplicationContextSourceApis();
app.ApplicationTodoRabbitApis();
app.ApplicationRbacApis();
app.ApplicationIeltsApis();
app.ApplicationComponentSplitterApis();
app.ApplicationNoteApis();

// Observability REST + SSE endpoints (mapped directly, not via MiddlewareDependencyInversion).
LogEndpoints.MapLogApis(app);

// SPA fallback — declared AFTER every API route so it can never swallow one.
// vue-router uses real paths (/ielts, /5lakhs), so a refresh or a deep link
// issues a normal GET for a path the server has no file for; without this it
// would 404. Requests under /api, /public and /swagger are left alone so a
// genuine API 404 still reads as a 404 rather than as a page of HTML.
app.MapFallback(async ctx =>
{
    var path = ctx.Request.Path.Value ?? "/";

    var isApiPath = path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/public", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase);

    var indexHtml = Path.Combine(app.Environment.WebRootPath ?? "", "index.html");

    if (isApiPath || !File.Exists(indexHtml))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.SendFileAsync(indexHtml);
});

app.UseCors();

// Create the SQLite file + tables on first run, then bring an older db up to the new schema.
using (var scope = app.Services.CreateScope())
{
	var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
	app.Logger.LogInformation("SQLite database: {Path}", sqliteConnectionString);
	db.Database.EnsureCreated();   // creates everything fresh if the file is new
	SchemaUpgrade.Run(db);         // one-time, idempotent upgrade for older db files

	// RBAC. Both steps are idempotent, and the order matters: give every existing
	// row a role before seeding, so the admin count the guardrails read is right
	// from the first request. The seed secret is never logged.
	RbacSeeder.NormalizeExistingRoles(db);
	RbacSeeder.Run(db, app.Logger);

	// Shared IELTS demo content. After RBAC so roles exist first, and idempotent
	// by SeedKey: restarting never duplicates, and editing a seed in code
	// updates the stored test in place.
	RabbitIC.Api.Ielts.IeltsReadingSeeder.Run(db, app.Logger);

	// Warm the vector index from SQLite so retrieval works from the first request.
	var vectorIndex = scope.ServiceProvider.GetRequiredService<RabbitIC.Api.Retrieval.VectorIndex>();
	vectorIndex.Load(db.EmbeddingChunks.AsNoTracking());
	app.Logger.LogInformation("VectorIndex loaded with {Count} chunks.", vectorIndex.Count);
	
	// Purge old deleted notes
	var noteService = scope.ServiceProvider.GetRequiredService<RabbitIC.Api.Services.INoteService>();
	noteService.PurgeOldDeletedNotesAsync().GetAwaiter().GetResult();

	// Startup system log — environment, version, DB provider/name, .NET version.
	// Never logs the connection string or credentials.
	var startupQueue = scope.ServiceProvider.GetRequiredService<ILogQueue>();
	var startupEnv   = app.Environment.EnvironmentName;
	var asm          = System.Reflection.Assembly.GetExecutingAssembly();
	var dbNamePart   = Path.GetFileName(sqliteConnectionString);
	startupQueue.Enqueue(new RabbitIC.Api.Models.ActivityLog
	{
		TimestampUtc = DateTime.UtcNow,
		Level        = "Information",
		Category     = "system",
		Message      = $"App started. Env={startupEnv} · DB={dbNamePart} · .NET={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} · v={asm.GetName().Version}",
		Environment  = startupEnv,
		MachineName  = System.Net.Dns.GetHostName(),
	});
}


app.Run();
