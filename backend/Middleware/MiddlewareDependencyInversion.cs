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

        /// <summary>
        /// RBAC. Both groups mount under /api: /api/me/tabs for the caller's own
        /// tabs and /api/admin/rbac/* for administering other users.
        /// </summary>
        public static WebApplication ApplicationIeltsApis(this WebApplication app)
        {
            var ieltsApi = app.MapGroup("/api").WithTags("IELTS");

            ieltsApi.MapIeltsApis();
            return app;
        }

        /// <summary>
        /// The Angular component splitter. Mounts under /api, which puts it
        /// behind the "component-splitter" gate in TabAccessMiddleware.
        /// </summary>
        public static WebApplication ApplicationComponentSplitterApis(this WebApplication app)
        {
            var splitterApi = app.MapGroup("/api").WithTags("Component splitter");

            splitterApi.MapComponentSplitterApis();
            return app;
        }


        public static WebApplication ApplicationRbacApis(this WebApplication app)
        {
            var rbacApi = app.MapGroup("/api").WithTags("RBAC");

            rbacApi.MapRbacApis();
            return app;
        }

        public static WebApplication ApplicationSearchApis(this WebApplication app)
        {
            var searchApi = app.MapGroup("/api").WithTags("Web search");

            searchApi.MapSearchApis();
            searchApi.MapSearchHistoryApis();
            return app;
        }

        /// <summary>
        /// Oddity. The admin API mounts under /api (behind the "oddity" tab gate);
        /// the gateway itself under /oddity/api, authenticated by its own keys.
        /// </summary>
        public static WebApplication ApplicationOddityApis(this WebApplication app)
        {
            var adminApi = app.MapGroup("/api").WithTags("Oddity");

            adminApi.MapOddityAdminApis();
            app.MapOddityGatewayApis();
            return app;
        }

        public static WebApplication ApplicationNoteApis(this WebApplication app)
        {
            app.MapNoteApis();
            return app;
        }
    }
}
