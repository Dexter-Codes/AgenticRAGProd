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
    [Fact]
    public void ObsoleteSourceContainerSettingDoesNotBlockMessageOnlyChat()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CosmosDb:ConnectionString"] = "local-placeholder-only",
            ["CosmosDb:ContainerName"] = "chat",
            ["CosmosDb:SourcesContainerName"] = "chat"
        }).Build();
        var services = new ServiceCollection().AddLogging().AddInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        // Old external source settings are ignored; resolving options creates no Azure clients.
        var options = provider.GetRequiredService<IOptions<CosmosDbOptions>>().Value;
        Assert.Equal("chat", options.ContainerName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CosmosStartupCredentialsAreRequiredOnlyWhenChatEnabled(bool enabled)
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
        // Validation only: no Cosmos/Search/embedding/storage clients are resolved.
        if (enabled) Assert.Throws<OptionsValidationException>(validator.Validate);
        else validator.Validate();
    }
}
