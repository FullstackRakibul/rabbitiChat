using RabbitIC.Api.MinimalApis;

namespace RabbitIC.Api.Middleware
{
    public static class MiddlewareDependencyInversion
    {
        public static WebApplication ApplyAppMiddleware(this WebApplication app)
        {
            return app;
        }

        public static WebApplication ApplicationPublicApis(this WebApplication app)
        {
            var publicApi = app.MapGroup("/public").WithTags("Public APIs");

            publicApi.MapPublicApis();
            return app;
        }

        public static WebApplication ApplicationOllamaApis(this WebApplication app)
        {
            var ollamaApi = app.MapGroup("/api").WithTags("Ollama APIs");

            ollamaApi.MapOllamaApis();
            return app;
        }
    }
}
