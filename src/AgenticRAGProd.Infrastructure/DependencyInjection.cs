using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using OpenAI.Embeddings;
using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Application.Conversations;
using AgenticRAGProd.Infrastructure.AI;
using AgenticRAGProd.Infrastructure.Chunking;
using AgenticRAGProd.Infrastructure.Configuration;
using AgenticRAGProd.Infrastructure.Parsing;
using AgenticRAGProd.Infrastructure.Persistence;
using AgenticRAGProd.Infrastructure.Search;
using AgenticRAGProd.Infrastructure.Storage;

namespace AgenticRAGProd.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<AzureOpenAiOptions>()
            .Bind(configuration.GetSection(
                AzureOpenAiOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options =>
                    Uri.TryCreate(
                        options.Endpoint,
                        UriKind.Absolute,
                        out var endpoint)
                    && endpoint.Scheme == Uri.UriSchemeHttps,
                "AzureOpenAI:Endpoint must be an absolute HTTPS URL.")
            .ValidateOnStart();

        // A fresh kernel per resolution avoids shared mutable kernel state.
        services.AddTransient<Kernel>(serviceProvider =>
        {
            var options = serviceProvider
                .GetRequiredService<IOptions<AzureOpenAiOptions>>()
                .Value;

            var builder = Kernel.CreateBuilder();

            builder.AddAzureOpenAIChatCompletion(
                deploymentName: options.ChatDeployment,
                endpoint: options.Endpoint,
                apiKey: options.ApiKey);

            return builder.Build();
        });

        services.AddScoped<IAnswerGenerator,
            SemanticKernelAnswerGenerator>();

        services.AddSingleton<AzureOpenAIClient>(serviceProvider =>
        {
            var options = serviceProvider
                .GetRequiredService<IOptions<AzureOpenAiOptions>>()
                .Value;

            return new AzureOpenAIClient(
                new Uri(options.Endpoint),
                new AzureKeyCredential(options.ApiKey));
        });

        services.AddSingleton<EmbeddingClient>(serviceProvider =>
        {
            var options = serviceProvider
                .GetRequiredService<IOptions<AzureOpenAiOptions>>()
                .Value;

            return serviceProvider
                .GetRequiredService<AzureOpenAIClient>()
                .GetEmbeddingClient(options.EmbeddingDeployment);
        });

        services.AddScoped<ITextEmbeddingService,
            AzureOpenAiEmbeddingService>();

        services
            .AddOptions<AzureStorageOptions>()
            .Bind(configuration.GetSection(
                AzureStorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<BlobContainerClient>(serviceProvider =>
        {
            var options = serviceProvider
                .GetRequiredService<IOptions<AzureStorageOptions>>()
                .Value;

            return new BlobContainerClient(
                options.ConnectionString,
                options.ContainerName);
        });

        services.AddScoped<IDocumentStorage, BlobDocumentStorage>();
        services.AddScoped<IDocumentParser, PdfDocumentParser>();

        services
            .AddOptions<ChunkingOptions>()
            .Bind(configuration.GetSection(
                ChunkingOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                settings =>
                    settings.OverlapTokens < settings.MaxTokens / 2,
                "Chunk overlap must be less than half the chunk budget.")
            .ValidateOnStart();

        services.AddScoped<IDocumentChunker, TokenDocumentChunker>();

        services
            .AddOptions<AzureSearchOptions>()
            .Bind(configuration.GetSection(
                AzureSearchOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(settings => settings.FinalContextCount <= settings.CandidateCount,
                "AzureSearch:FinalContextCount must not exceed CandidateCount.")
            .Validate(
                options =>
                    Uri.TryCreate(
                        options.Endpoint,
                        UriKind.Absolute,
                        out var endpoint)
                    && endpoint.Scheme == Uri.UriSchemeHttps,
                "AzureSearch:Endpoint must be an absolute HTTPS URL.")
            .ValidateOnStart();

        services.AddSingleton<SearchClient>(serviceProvider =>
        {
            var options = serviceProvider
                .GetRequiredService<IOptions<AzureSearchOptions>>()
                .Value;

            return new SearchClient(
                new Uri(options.Endpoint),
                options.IndexName,
                new AzureKeyCredential(options.ApiKey));
        });

        services.AddScoped<ITextTokenCounter, EmbeddingTokenCounter>();

        services.AddScoped<IChunkIndexWriter, AzureSearchChunkIndexWriter>();
        services.AddScoped<ICorpusChunkRetriever, AzureSearchChunkRetriever>();
        services.AddScoped<IPageLayoutAnalyzer, PageLayoutAnalyzer>();
        services.AddOptions<ChatOptions>()
            .Bind(configuration.GetSection("Chat"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Keep the DI graph valid even when chat is disabled, but require Cosmos
        // credentials at startup only when its Development routes are enabled.
        // CosmosClient is lazy and its constructor provisions nothing.
        var cosmosOptions = services.AddOptions<CosmosDbOptions>()
            .Bind(configuration.GetSection(CosmosDbOptions.SectionName))
            .ValidateDataAnnotations();
        if (configuration.GetValue<bool>("Chat:LocalSingleUserMode"))
            cosmosOptions.ValidateOnStart();

        services.AddSingleton<CosmosClient>(serviceProvider =>
        {
            var settings = serviceProvider
                .GetRequiredService<IOptions<CosmosDbOptions>>()
                .Value;

            return new CosmosClient(
                settings.ConnectionString,
                new CosmosClientOptions
                {
                    ApplicationName = "AgenticRAGProd",
                    ConnectionMode = ConnectionMode.Gateway,
                    RequestTimeout = TimeSpan.FromSeconds(
                        settings.RequestTimeoutSeconds),
                    UseSystemTextJsonSerializerWithOptions =
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)
                });
        });

        services.AddScoped<IConversationStore, CosmosConversationStore>();

        return services;
    }
}
