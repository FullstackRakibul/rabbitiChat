// /api/admin/model-providers — SystemAdmin only, like /api/admin/rbac. Every route resolves the
// session, refuses anyone else with 403, and hands the input to ModelProviderService; no route
// decides a rule. Nothing returned here ever carries a key — only its last 4.

using Microsoft.AspNetCore.DataProtection;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;
using RabbitIC.Api.Rbac;

namespace RabbitIC.Api.ModelProviders;

public static class ModelProviderApis
{
    private const string AdminOnly = "Model providers are managed by SystemAdmin only.";

    public static WebApplication ApplicationModelProviderApis(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/model-providers").WithTags("Model providers");

        group.MapGet("/", async (HttpContext ctx, AppDbContext db, ModelProviderService svc, CancellationToken ct) =>
            await AdminAsync(ctx, db) is { } refused ? refused : Results.Ok(await svc.ListAsync(ct)))
            .WithSummary("Every provider (no keys) with this month's tokens.");

        group.MapPost("/", async (ModelProviderInput input, HttpContext ctx, AppDbContext db, ModelProviderService svc, CancellationToken ct) =>
            await AdminAsync(ctx, db) is { } refused ? refused : Map(await svc.CreateAsync(input, ct)))
            .WithSummary("Add a provider: key, name, base URL, API key, capabilities.");

        group.MapPut("/{id:int}", async (int id, ModelProviderInput input, HttpContext ctx, AppDbContext db, ModelProviderService svc, CancellationToken ct) =>
            await AdminAsync(ctx, db) is { } refused ? refused : Map(await svc.UpdateAsync(id, input, ct)))
            .WithSummary("Edit a provider. A blank API key keeps the stored one.");

        group.MapDelete("/{id:int}", async (int id, HttpContext ctx, AppDbContext db, ModelProviderService svc, CancellationToken ct) =>
        {
            if (await AdminAsync(ctx, db) is { } refused) return refused;
            var r = await svc.DeleteAsync(id, ct);
            return r.Outcome == ProviderOutcome.Ok ? Results.NoContent() : Map(r);
        })
        .WithSummary("Remove a provider. Refused while Jiji's settings point at it. Usage history stays.");

        group.MapPost("/{id:int}/test", async (int id, HttpContext ctx, AppDbContext db, ModelProviderService svc, CancellationToken ct) =>
            await AdminAsync(ctx, db) is { } refused ? refused : Map(await svc.TestAsync(id, ct)))
            .WithSummary("GET /models, a 5-token chat call with stream false, then streamed. Returns each step.");

        group.MapGet("/{id:int}/catalog", async (int id, HttpContext ctx, AppDbContext db, ModelProviderService svc, CancellationToken ct) =>
            await AdminAsync(ctx, db) is { } refused ? refused : Map(await svc.CatalogAsync(id, ct)))
            .WithSummary("The stored catalog with the allowed and tool-capable lists.");

        group.MapPost("/{id:int}/catalog/refresh", async (int id, HttpContext ctx, AppDbContext db, ModelProviderService svc, CancellationToken ct) =>
            await AdminAsync(ctx, db) is { } refused ? refused : Map(await svc.RefreshCatalogAsync(id, ct)))
            .WithSummary("Fetch GET /models again.");

        group.MapPut("/{id:int}/models", async (int id, ModelProviderModelsInput input, HttpContext ctx, AppDbContext db, ModelProviderService svc, CancellationToken ct) =>
            await AdminAsync(ctx, db) is { } refused ? refused : Map(await svc.SaveModelsAsync(id, input, ct)))
            .WithSummary("Save the allowed and tool-capable models.");

        return app;
    }

    /// <summary>
    /// /api/admin/rbac/model-access — Access → Models. Open to whoever may manage access (SystemAdmin or
    /// the "Manage access" permission), the same gate as the rest of /api/admin/rbac.
    /// </summary>
    public static WebApplication ApplicationModelAccessApis(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin/rbac/model-access").WithTags("Model access");

        g.MapGet("/catalog", async (HttpContext ctx, AppDbContext db, Services.RbacService rbac, ModelAccessAdmin admin, CancellationToken ct) =>
            (await ManagerAsync(ctx, db, rbac, ct)).Refused ?? Results.Ok(await admin.CatalogAsync(ct)))
            .WithSummary("Every model a rule can name, grouped by source with multipliers, and the module defaults.");

        g.MapGet("/roles/{role}", async (string role, HttpContext ctx, AppDbContext db, Services.RbacService rbac, ModelAccessAdmin admin, CancellationToken ct) =>
            (await ManagerAsync(ctx, db, rbac, ct)).Refused ?? Results.Ok(await admin.RoleAsync(role, ct)));

        g.MapPut("/roles/{role}", async (string role, ModelRulesDto input, HttpContext ctx, AppDbContext db, Services.RbacService rbac, ModelAccessAdmin admin, CancellationToken ct) =>
        {
            var (refused, user) = await ManagerAsync(ctx, db, rbac, ct);
            if (refused is not null) return refused;
            var error = await admin.SaveRoleAsync(role, input, user!.Id, ct);
            return error is null ? Results.Ok(await admin.RoleAsync(role, ct)) : Results.Json(new { error }, statusCode: 400);
        });

        g.MapGet("/users/{id:int}", async (int id, HttpContext ctx, AppDbContext db, Services.RbacService rbac, ModelAccessAdmin admin, CancellationToken ct) =>
        {
            var (refused, _) = await ManagerAsync(ctx, db, rbac, ct);
            if (refused is not null) return refused;
            return await admin.UserAsync(id, ct) is { } dto ? Results.Ok(dto) : Results.Json(new { error = "No such user." }, statusCode: 404);
        });

        g.MapPut("/users/{id:int}", async (int id, ModelRulesDto input, HttpContext ctx, AppDbContext db, Services.RbacService rbac, ModelAccessAdmin admin, CancellationToken ct) =>
        {
            var (refused, user) = await ManagerAsync(ctx, db, rbac, ct);
            if (refused is not null) return refused;
            var error = await admin.SaveUserAsync(id, input, user!.Id, ct);
            return error is null ? Results.Ok(await admin.UserAsync(id, ct)) : Results.Json(new { error }, statusCode: 400);
        });

        g.MapPut("/defaults/{module}", async (string module, ModelDefaultDto input, HttpContext ctx, AppDbContext db, Services.RbacService rbac, ModelAccessAdmin admin, CancellationToken ct) =>
        {
            var (refused, user) = await ManagerAsync(ctx, db, rbac, ct);
            if (refused is not null) return refused;
            var error = await admin.SetDefaultAsync(module, input, user!.Id, ct);
            return error is null ? Results.Ok(await admin.CatalogAsync(ct)) : Results.Json(new { error }, statusCode: 400);
        })
        .WithSummary("Set a module's default model, e.g. the HR default every locked user gets.");

        return app;
    }

    private static async Task<(IResult? Refused, User? User)> ManagerAsync(HttpContext ctx, AppDbContext db, Services.RbacService rbac, CancellationToken ct)
    {
        var user = await Authorization.ResolveUser(ctx, db);
        if (user is null) return (Results.Unauthorized(), null);
        return await rbac.CanManageAsync(user, ct)
            ? (null, user)
            : (Results.Json(new { error = "Managing access requires SystemAdmin or the Manage access permission." }, statusCode: StatusCodes.Status403Forbidden), user);
    }

    /// <summary>Null for a SystemAdmin; otherwise the refusal to return.</summary>
    private static async Task<IResult?> AdminAsync(HttpContext ctx, AppDbContext db)
    {
        var user = await Authorization.ResolveUser(ctx, db);
        if (user is null) return Results.Unauthorized();
        return AccessPolicy.IsSuperUser(user)
            ? null
            : Results.Json(new { error = AdminOnly }, statusCode: StatusCodes.Status403Forbidden);
    }

    private static IResult Map<T>(ProviderResult<T> r) => r.Outcome switch
    {
        ProviderOutcome.Ok => Results.Ok(r.Value),
        ProviderOutcome.NotFound => Results.Json(new { error = r.Error }, statusCode: StatusCodes.Status404NotFound),
        ProviderOutcome.Conflict => Results.Json(new { error = r.Error }, statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new { error = r.Error, errors = r.Errors }, statusCode: StatusCodes.Status400BadRequest),
    };
}

public static class ModelProviderModule
{
    /// <summary>
    /// Data Protection with the key ring beside the database (/data/keys on Fly), so a redeploy can still
    /// decrypt the stored keys; DataProtection:KeysPath overrides it. Then the client, the provider and the service.
    /// </summary>
    public static void AddModelProviders(IServiceCollection services, IConfiguration config, string? databaseDirectory)
    {
        var keysPath = config["DataProtection:KeysPath"];
        if (string.IsNullOrWhiteSpace(keysPath))
            keysPath = Path.Combine(databaseDirectory ?? AppContext.BaseDirectory, "keys");
        Directory.CreateDirectory(keysPath);
        services.AddDataProtection()
            .SetApplicationName("Mostisko")
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

        // No base address and no default headers: each request carries its provider's URL and key.
        services.AddHttpClient(DbModelSourceProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(120));
        // Speech: each call sets its own limit (a test, a preview, a long narration part).
        services.AddHttpClient(SpeechClient.ClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<SpeechClient>();
        services.AddSingleton<ModelProviderKeys>();
        services.AddSingleton<ModelUsageRecorder>();
        services.AddHostedService(sp => sp.GetRequiredService<ModelUsageRecorder>());
        services.AddSingleton<DbModelSourceProvider>();
        services.AddScoped<ModelProviderService>();
        services.AddScoped<ModelAccess>();
        services.AddScoped<ModelAccessAdmin>();
    }
}
