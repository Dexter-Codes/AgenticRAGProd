using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AgenticRAGProd.Application;
using AgenticRAGProd.Infrastructure;
using AgenticRAGProd.Infrastructure.Configuration;
using Xunit;

namespace AgenticRAGProd.Tests.Chat;

public sealed class ChatRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupAndSqliteRegistrationDoNotRequireCosmosCredentials(bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Chat:LocalSingleUserMode"] = enabled.ToString(),
            ["AzureOpenAI:Endpoint"] = "https://local.invalid",
            ["AzureOpenAI:ApiKey"] = "local-test-key",
            ["AzureOpenAI:ChatDeployment"] = "local-chat",
            ["AzureOpenAI:EmbeddingDeployment"] = "local-embedding",
            ["AzureSearch:Endpoint"] = "https://local.invalid",
            ["AzureSearch:ApiKey"] = "local-test-key",
            ["AzureSearch:IndexName"] = "local-index",
            ["AzureStorage:ConnectionString"] = "UseDevelopmentStorage=true",
            ["AzureStorage:ContainerName"] = "local-documents"
        }).Build();
        var services = new ServiceCollection().AddLogging().AddApplication().AddInfrastructure(configuration);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var validator = provider.GetRequiredService<IStartupValidator>();
        // No model, search or blob calls are made by validation or store resolution.
        validator.Validate();
        Assert.IsType<AgenticRAGProd.Infrastructure.Persistence.SqliteConversationStore>(
            provider.GetRequiredService<AgenticRAGProd.Application.Abstractions.IConversationStore>());
    }
}
