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

        public static WebApplication ApplicationDocumentApis(this WebApplication app)
        {
            var documentApi = app.MapGroup("/api").WithTags("Document APIs");

            documentApi.MapDocumentApis();
            return app;
        }

        public static WebApplication ApplicationBuyerProfileApis(this WebApplication app)
        {
            var profileApi = app.MapGroup("/api").WithTags("Buyer profiles");

            profileApi.MapBuyerProfileApis();
            return app;
        }

        public static WebApplication ApplicationOllamaApis(this WebApplication app)
        {
            var ollamaApi = app.MapGroup("/api").WithTags("Ollama APIs");

            ollamaApi.MapOllamaApis();
            return app;
        }

        public static WebApplication ApplicationTodoRabbitApis(this WebApplication app)
        {
            var todoRabbitApi = app.MapGroup("/api").WithTags("todorabbit");

            todoRabbitApi.MapTodoRabbitApis();
            return app;
        }

        public static WebApplication ApplicationContextSourceApis(this WebApplication app)
        {
            var contextApi = app.MapGroup("/api").WithTags("Context sources");

            contextApi.MapContextSourceApis();
            return app;
        }

        public static WebApplication ApplicationSearchApis(this WebApplication app)
        {
            var searchApi = app.MapGroup("/api").WithTags("Web search");

            searchApi.MapSearchApis();
            return app;
        }
    }
}
