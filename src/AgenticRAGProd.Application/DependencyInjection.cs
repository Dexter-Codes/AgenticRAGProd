using Microsoft.Extensions.DependencyInjection;
using AgenticRAGProd.Application.Documents;
using AgenticRAGProd.Application.Conversations;

namespace AgenticRAGProd.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services)
    {
        services.AddScoped<DocumentIndexingService>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<ChatTelemetry>();
        services.AddScoped<ChatService>();
        services.AddScoped<ChatAnswerService>();
        return services;
    }
}
