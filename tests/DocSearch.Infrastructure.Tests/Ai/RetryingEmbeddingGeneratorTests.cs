using System.Net;
using DocSearch.Application.Exceptions;
using DocSearch.Infrastructure.Ai.Decorators;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DocSearch.Infrastructure.Tests.Ai;

public sealed class RetryingEmbeddingGeneratorTests
{
    private static readonly float[] _unitVector = [1f];
    private static readonly TimeSpan[] _shortBackoffs = [TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5)];
    private static readonly TimeSpan[] _singleBackoff = [TimeSpan.FromMilliseconds(5)];

    [Fact]
    public async Task Retries_On_Transient_Http500_Then_Succeeds()
    {
        var attempts = 0;
        var inner = new StubEmbeddingGenerator(inputs =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw new HttpRequestException("Simulated transient 500", null, HttpStatusCode.InternalServerError);
            }
            return [new Embedding<float>(_unitVector)];
        });

        var logger = new TestLogger<RetryingEmbeddingGenerator>();
        var retrier = new RetryingEmbeddingGenerator(
            inner,
            logger,
            maxAttempts: 3,
            backoffs: _shortBackoffs);

        var result = await retrier.GenerateAsync(["test"]);

        Assert.Single(result);
        Assert.Equal(3, attempts);
        Assert.Equal(2, logger.Logs.Count(l => l.LogLevel == LogLevel.Warning));
    }

    [Fact]
    public async Task Retries_On_Timeout_Then_Succeeds()
    {
        var attempts = 0;
        var inner = new StubEmbeddingGenerator(inputs =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new TimeoutException("Simulated socket timeout");
            }
            return [new Embedding<float>(_unitVector)];
        });

        var logger = new TestLogger<RetryingEmbeddingGenerator>();
        var retrier = new RetryingEmbeddingGenerator(
            inner,
            logger,
            maxAttempts: 3,
            backoffs: _singleBackoff);

        var result = await retrier.GenerateAsync(["test"]);

        Assert.Single(result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Does_Not_Retry_On_Http400()
    {
        var attempts = 0;
        var inner = new StubEmbeddingGenerator(inputs =>
        {
            attempts++;
            throw new HttpRequestException("Client error", null, HttpStatusCode.BadRequest);
        });

        var logger = new TestLogger<RetryingEmbeddingGenerator>();
        var retrier = new RetryingEmbeddingGenerator(inner, logger, maxAttempts: 3);

        var ex = await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            retrier.GenerateAsync(["test"]));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Does_Not_Retry_When_Inner_Throws_Wrapped_Http400()
    {
        var attempts = 0;
        var inner = new StubEmbeddingGenerator(inputs =>
        {
            attempts++;
            throw new ExternalServiceUnavailableException(
                "Embedding",
                "Embedding service is currently unavailable.",
                new HttpRequestException("Bad request", null, HttpStatusCode.BadRequest));
        });

        var logger = new TestLogger<RetryingEmbeddingGenerator>();
        var retrier = new RetryingEmbeddingGenerator(inner, logger, maxAttempts: 3, backoffs: _shortBackoffs);

        var ex = await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            retrier.GenerateAsync(["test"]));

        Assert.Equal(1, attempts);
        Assert.Contains("unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Does_Not_Retry_When_Inner_Throws_Wrapped_ContextLength_Error()
    {
        var attempts = 0;
        var inner = new StubEmbeddingGenerator(inputs =>
        {
            attempts++;
            throw new ExternalServiceUnavailableException(
                "Embedding",
                "Embedding service is currently unavailable.",
                new InvalidOperationException("the input length exceeds the context length"));
        });

        var logger = new TestLogger<RetryingEmbeddingGenerator>();
        var retrier = new RetryingEmbeddingGenerator(inner, logger, maxAttempts: 3, backoffs: _shortBackoffs);

        var ex = await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            retrier.GenerateAsync(["test"]));

        Assert.Equal(1, attempts);
        Assert.Contains("unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Retries_On_Wrapped_Http429_TooManyRequests_Then_Succeeds()
    {
        var attempts = 0;
        var inner = new StubEmbeddingGenerator(inputs =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new ExternalServiceUnavailableException(
                    "Embedding",
                    "Embedding service is currently unavailable.",
                    new HttpRequestException("Rate limit exceeded", null, HttpStatusCode.TooManyRequests));
            }
            return [new Embedding<float>(_unitVector)];
        });

        var logger = new TestLogger<RetryingEmbeddingGenerator>();
        var retrier = new RetryingEmbeddingGenerator(inner, logger, maxAttempts: 3, backoffs: _singleBackoff);

        var result = await retrier.GenerateAsync(["test"]);

        Assert.Single(result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Does_Not_Retry_When_Caller_CancellationToken_Canceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var attempts = 0;
        var inner = new StubEmbeddingGenerator(inputs =>
        {
            attempts++;
            cts.Token.ThrowIfCancellationRequested();
            return [];
        });

        var logger = new TestLogger<RetryingEmbeddingGenerator>();
        var retrier = new RetryingEmbeddingGenerator(inner, logger, maxAttempts: 3);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            retrier.GenerateAsync(["test"], cancellationToken: cts.Token));

        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task Throws_ExternalServiceUnavailableException_After_Exhausting_Retries()
    {
        var attempts = 0;
        var inner = new StubEmbeddingGenerator(inputs =>
        {
            attempts++;
            throw new HttpRequestException("Continuous 503", null, HttpStatusCode.ServiceUnavailable);
        });

        var logger = new TestLogger<RetryingEmbeddingGenerator>();
        var retrier = new RetryingEmbeddingGenerator(
            inner,
            logger,
            maxAttempts: 3,
            backoffs: _shortBackoffs);

        var ex = await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            retrier.GenerateAsync(["test"]));

        Assert.Equal(3, attempts);
        Assert.Contains("unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Never_Logs_Document_Text_In_Retry_Warnings()
    {
        const string sensitiveDocText = "محتوای کاملا محرمانه و اختصاصی سند دانشگاه";
        var inner = new StubEmbeddingGenerator(inputs =>
            throw new HttpRequestException("Network failure", null, HttpStatusCode.BadGateway));

        var logger = new TestLogger<RetryingEmbeddingGenerator>();
        var retrier = new RetryingEmbeddingGenerator(
            inner,
            logger,
            maxAttempts: 2,
            backoffs: _singleBackoff);

        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            retrier.GenerateAsync([sensitiveDocText]));

        Assert.NotEmpty(logger.Logs);
        foreach (var log in logger.Logs)
        {
            Assert.DoesNotContain(sensitiveDocText, log.Message, StringComparison.Ordinal);
        }
    }

    private sealed class StubEmbeddingGenerator(Func<IEnumerable<string>, IList<Embedding<float>>> generatorFunc)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(generatorFunc(values)));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<(LogLevel LogLevel, string Message)> Logs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception)));
        }
    }
}
