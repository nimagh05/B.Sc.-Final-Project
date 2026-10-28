using DocSearch.Application.Exceptions;
using DocSearch.Domain.Constants;
using Microsoft.Extensions.AI;

namespace DocSearch.Infrastructure.Ai.Decorators;

public sealed class BatchingEmbeddingGenerator : DelegatingEmbeddingGenerator<string, Embedding<float>>
{
    private readonly int _batchSize;
    private readonly int _dimensions;
    private readonly int _pauseBetweenBatchesMs;
    private readonly TimeProvider _timeProvider;

    public BatchingEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> innerGenerator,
        int batchSize = 4,
        int dimensions = EmbeddingConstants.VectorDimensions,
        int pauseBetweenBatchesMs = 0,
        TimeProvider? timeProvider = null)
        : base(innerGenerator)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(pauseBetweenBatchesMs);

        _batchSize = batchSize;
        _dimensions = dimensions;
        _pauseBetweenBatchesMs = pauseBetweenBatchesMs;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();

        var list = values as IList<string> ?? values.ToList();
        if (list.Count == 0)
        {
            return new GeneratedEmbeddings<Embedding<float>>();
        }

        var allEmbeddings = new List<Embedding<float>>(list.Count);

        for (int i = 0; i < list.Count; i += _batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (i > 0 && _pauseBetweenBatchesMs > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_pauseBetweenBatchesMs), _timeProvider, cancellationToken).ConfigureAwait(false);
            }

            var batchCount = Math.Min(_batchSize, list.Count - i);
            var batch = new string[batchCount];
            for (int b = 0; b < batchCount; b++)
            {
                batch[b] = list[i + b];
            }

            var response = await InnerGenerator.GenerateAsync(batch, options, cancellationToken).ConfigureAwait(false);

            if (response == null || response.Count != batchCount)
            {
                throw new ExternalServiceUnavailableException(
                    "Embedding",
                    $"Embedding provider returned unexpected vector count (expected {batchCount}, got {response?.Count ?? 0}).");
            }

            foreach (var embedding in response)
            {
                if (embedding.Vector.Length != _dimensions)
                {
                    throw new ExternalServiceUnavailableException(
                        "Embedding",
                        $"Embedding dimension mismatch: expected {_dimensions}, got {embedding.Vector.Length}.");
                }

                var vec = embedding.Vector.ToArray();
                double sumSq = 0.0;
                for (int k = 0; k < vec.Length; k++)
                {
                    sumSq += (double)vec[k] * vec[k];
                }

                double norm = Math.Sqrt(sumSq);
                if (norm > 0.0)
                {
                    float invNorm = (float)(1.0 / norm);
                    for (int k = 0; k < vec.Length; k++)
                    {
                        vec[k] *= invNorm;
                    }
                }

                allEmbeddings.Add(new Embedding<float>(vec));
            }
        }

        return new GeneratedEmbeddings<Embedding<float>>(allEmbeddings);
    }
}
