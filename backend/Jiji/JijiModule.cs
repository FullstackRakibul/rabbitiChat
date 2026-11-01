// Jiji — the module's two hooks into Program.cs: services in, routes out.
// Day 1: options, the content service and the read endpoints. Day 2: ideas, scripts, checks, approval.
// Day 3: storage, Azure Speech, the Tts handler and the worker. The renderer and the YouTube
// publisher register here as they arrive: a handler registered is a job type the worker claims.

using RabbitIC.Api.Jiji.Services;
using RabbitIC.Api.Jiji.Workers;

namespace RabbitIC.Api.Jiji;

public static class JijiModule
{
    public static IServiceCollection AddJijiModule(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<JijiOptions>(config.GetSection(JijiOptions.Section));
        services.AddScoped<JijiLlm>();
        services.AddScoped<IdeaGenerator>();
        services.AddScoped<ScriptGenerator>();
        services.AddScoped<JijiContentService>();

        services.AddSingleton<JijiStorage>();
        services.AddHttpClient(TtsService.ClientName, c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddScoped<TtsService>();
        services.AddScoped<IJijiJobHandler, TtsJobHandler>();
        services.AddHostedService<JijiWorker>();
        return services;
    }

    /// <summary>/api/jiji/*, gated by the `jiji` tab (TabAccessMiddleware).</summary>
    public static WebApplication ApplicationJijiApis(this WebApplication app)
    {
        app.MapGroup("/api").WithTags("Jiji").MapJijiApis();
        return app;
    }
}
