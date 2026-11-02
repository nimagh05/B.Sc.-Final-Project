using System.ClientModel;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Options;
using DocSearch.Infrastructure.Ai.Decorators;
using DocSearch.Infrastructure.Ai.Fakes;
using DocSearch.Infrastructure.Ai.Ollama;
using DocSearch.Infrastructure.Extraction;
using DocSearch.Infrastructure.Health;
using DocSearch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;

namespace DocSearch.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default");

        services.AddDbContext<AppDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                npgsqlOptions.UseVector();
            });
        });

        var extractionSection = configuration.GetSection(ExtractionOptions.SectionName);
        var extractorBaseUrl = extractionSection.GetValue<string>("ExtractorBaseUrl") ?? "http://127.0.0.1:8000";
        if (!extractorBaseUrl.EndsWith('/'))
        {
            extractorBaseUrl += "/";
        }

        services.AddHttpClient<IPdfExtractor, HttpPdfExtractor>(client =>
        {
            client.BaseAddress = new Uri(extractorBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(120);
        });

        services.AddHttpClient("ExtractorHealthCheck", client =>
        {
            client.BaseAddress = new Uri(extractorBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(3);
        });

        services.AddScoped<IVectorStore, PgVectorStore>();
        services.AddSingleton<IFileStorage, Storage.LocalFileStorage>();
        services.AddSingleton<IIngestionQueue, Ingestion.IngestionQueue>();
        services.AddScoped<IIngestionService, Ingestion.IngestionService>();
        services.AddScoped<IAccessService, Identity.AccessService>();
        services.AddScoped<IRagService, Rag.RagService>();

        services.AddEmbeddingProvider(configuration);
        services.AddChatProvider(configuration);

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
            .AddCheck<ExtractorHealthCheck>("extractor", tags: ["ready"])
            .AddCheck<EmbeddingHealthCheck>("embedding", tags: ["ready"]);

        return services;
    }

    public static IServiceCollection AddChatProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient("OllamaChatClient", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<AiOptions>>().Value.Chat;
            var endpoint = options.Endpoint;
            if (!endpoint.EndsWith('/'))
            {
                endpoint += "/";
            }

            client.BaseAddress = new Uri(endpoint);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        services.AddSingleton<IChatClient>(sp =>
        {
            var aiOptions = sp.GetRequiredService<IOptions<AiOptions>>().Value.Chat;
            var config = sp.GetRequiredService<IConfiguration>();

            return aiOptions.Provider switch
            {
                "Fake" => new FakeChatClient(
                    config.GetValue<bool>("Ai:Chat:Fake:Fail"),
                    config.GetValue<int>("Ai:Chat:Fake:DelayMs")),
                "Ollama" => new OllamaApiClient(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("OllamaChatClient"),
                    aiOptions.Model),
                "OpenAICompatible" => CreateOpenAiChatClient(aiOptions),
                _ => throw new InvalidOperationException($"Unsupported chat provider: {aiOptions.Provider}")
            };
        });

        return services;
    }

    private static IChatClient CreateOpenAiChatClient(AiChatOptions options)
    {
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(options.Endpoint),
            NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        };
        var credential = new ApiKeyCredential(options.ApiKey ?? "none");
        var openAiClient = new OpenAIClient(credential, clientOptions);
        var chatClient = openAiClient.GetChatClient(options.Model);
        return chatClient.AsIChatClient();
    }

    public static IServiceCollection AddEmbeddingProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient("OllamaClient", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<AiOptions>>().Value.Embedding;
            var endpoint = options.Endpoint;
            if (!endpoint.EndsWith('/'))
            {
                endpoint += "/";
            }

            client.BaseAddress = new Uri(endpoint);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        services.AddHttpClient("EmbeddingHealthCheck", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<AiOptions>>().Value.Embedding;
            var endpoint = options.Endpoint;
            if (!endpoint.EndsWith('/'))
            {
                endpoint += "/";
            }

            client.BaseAddress = new Uri(endpoint);
            client.Timeout = TimeSpan.FromSeconds(3);
        });

        services.AddSingleton<IOllamaApiClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = factory.CreateClient("OllamaClient");
            var aiOpts = sp.GetRequiredService<IOptions<AiOptions>>().Value.Embedding;
            return new OllamaApiClient(httpClient, aiOpts.Model);
        });

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var aiOptions = sp.GetRequiredService<IOptions<AiOptions>>().Value.Embedding;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

            IEmbeddingGenerator<string, Embedding<float>> inner = aiOptions.Provider switch
            {
                "Fake" => new FakeEmbeddingGenerator(
                    sp.GetRequiredService<ITextNormalizer>(),
                    sp.GetRequiredService<IOptions<AiOptions>>()),
                "Ollama" => new OllamaEmbeddingGenerator(
                    sp.GetRequiredService<IOllamaApiClient>(),
                    sp.GetRequiredService<IOptions<AiOptions>>(),
                    loggerFactory.CreateLogger<OllamaEmbeddingGenerator>()),
                "OpenAICompatible" => CreateOpenAiGenerator(aiOptions),
                _ => throw new InvalidOperationException($"Unsupported embedding provider: {aiOptions.Provider}")
            };

            var retryLogger = loggerFactory.CreateLogger<RetryingEmbeddingGenerator>();
            var retrying = new RetryingEmbeddingGenerator(inner, retryLogger);
            var batching = new BatchingEmbeddingGenerator(
                retrying,
                aiOptions.BatchSize,
                aiOptions.Dimensions,
                aiOptions.PauseBetweenBatchesMs);

            return batching;
        });

        return services;
    }

    private static IEmbeddingGenerator<string, Embedding<float>> CreateOpenAiGenerator(AiEmbeddingOptions aiOptions)
    {
        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(aiOptions.Endpoint) };
        var credential = new ApiKeyCredential(aiOptions.ApiKey ?? "none");
        var openAiClient = new OpenAIClient(credential, clientOptions);
        var embeddingClient = openAiClient.GetEmbeddingClient(aiOptions.Model);
        return embeddingClient.AsIEmbeddingGenerator(aiOptions.Dimensions);
    }
}
