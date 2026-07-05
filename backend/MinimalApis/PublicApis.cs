namespace RabbitIC.Api.MinimalApis
{
    public static class PublicApis
    {
        public static IEndpointRouteBuilder MapPublicApis(this IEndpointRouteBuilder app)
        {
            app.MapGet("/", () => Results.Ok("pong"));
            
            
            var groupOne = app.MapGroup("/server").WithTags("server APIs");

            groupOne.MapGet("/", () => Results.Ok(new { Q = "What ? Server What ??" }))
                .WithName("server")
                .WithSummary("Get the status of the API.")
                .WithDescription("Returns a simple status message indicating that the API is running.");

            groupOne.MapGet("/status", () => Results.Ok(new { server = "running" }))
                .WithName("GetStatus")
                .WithSummary("Get the status of the API.")
                .WithDescription("Returns a simple status message indicating that the API is running.");



            var groupTwo = app.MapGroup("/backend").WithTags("backend APIs");

            groupTwo.MapGet("/products", () => Results.Ok(new { models = new[] { "rabbitiC", "calcifer" } }))
                .WithName("GetOllamaModels")
                .WithSummary("Get the list of available Ollama models.")
                .WithDescription("Returns a list of available Ollama models that can be used for inference.");

            return app;
        }
    }
}
