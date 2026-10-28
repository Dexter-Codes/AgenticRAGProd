using Microsoft.OpenApi;

namespace AgenticRAGProd.Api;

internal static class ApiDocumentation
{
    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "AgenticRAGProd API",
                Version = "v1",
                Description = "PDF upload, document indexing and Development-only persistent corpus chat. " +
                    "Executing Azure-backed operations uses the configured Azure services."
            });
        });
        return services;
    }

    public static void UseApiDocumentation(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("v1/swagger.json", "AgenticRAGProd API v1");
            options.DisplayRequestDuration();
        });
    }
}
