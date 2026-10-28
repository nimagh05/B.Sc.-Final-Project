using System.Globalization;
using DocSearch.Application.Exceptions;
using DocSearch.Infrastructure.Ai.Decorators;
using Microsoft.Extensions.AI;
using Xunit;

namespace DocSearch.Infrastructure.Tests.Ai;

public sealed class BatchingEmbeddingGeneratorTests
{
    private const int _dimensions = 4;
    private static readonly int[] _expectedBatchSizes = [4, 4, 2];
    private static readonly string[] _threeStrings = ["a", "b", "c"];
    private static readonly string[] _fourStrings = ["a", "b", "c", "d"];
    private static readonly float[] _vector34 = [3f, 4f, 0f, 0f];
    private static readonly float[] _zeroVector = [0f, 0f, 0f, 0f];

    [Fact]
    public async Task Batches_Are_Split_And_Reassembled_InOrder()
    {
        var batchSizes = new List<int>();
        var inner = new StubEmbeddingGenerator((inputs) =>
        {
            var list = inputs.ToList();
            batchSizes.Add(list.Count);
            return list.Select(text =>
            {
                var val = float.Parse(text, CultureInfo.InvariantCulture) + 1f;
                return new Embedding<float>(new float[] { val, 0f, 0f, 0f });
            }).ToList();
        });

        var batcher = new BatchingEmbeddingGenerator(inner, batchSize: 4, dimensions: _dimensions);

        var inputs = Enumerable.Range(0, 10).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
        var result = await batcher.GenerateAsync(inputs);

        Assert.Equal(10, result.Count);
        Assert.Equal(_expectedBatchSizes, batchSizes);

        for (int i = 0; i < 10; i++)
        {
            var vec = result[i].Vector.ToArray();
            Assert.Equal(1.0f, vec[0]); // L2-normalized from (i, 0, 0, 0)
        }
    }

    [Fact]
    public async Task Empty_Input_Returns_Empty()
    {
        var called = false;
        var inner = new StubEmbeddingGenerator(_ =>
        {
            called = true;
            return [];
        });

        var batcher = new BatchingEmbeddingGenerator(inner, batchSize: 4, dimensions: _dimensions);
        var result = await batcher.GenerateAsync(Array.Empty<string>());

        Assert.Empty(result);
        Assert.False(called);
    }

    [Fact]
    public async Task Single_Batch_Does_Not_Split()
    {
        var batchCalls = 0;
        var inner = new StubEmbeddingGenerator(inputs =>
        {
            batchCalls++;
            return inputs.Select(_ => new Embedding<float>(new float[_dimensions])).ToList();
        });

        var batcher = new BatchingEmbeddingGenerator(inner, batchSize: 4, dimensions: _dimensions);
        var result = await batcher.GenerateAsync(_threeStrings);

        Assert.Equal(3, result.Count);
        Assert.Equal(1, batchCalls);
    }

    [Fact]
    public async Task Throws_On_Dimension_Mismatch()
    {
        var inner = new StubEmbeddingGenerator(inputs =>
            inputs.Select(_ => new Embedding<float>(new float[2])).ToList()); // 2 != 4

        var batcher = new BatchingEmbeddingGenerator(inner, batchSize: 4, dimensions: _dimensions);

        var ex = await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            batcher.GenerateAsync(["test"]));

        Assert.Contains("dimension mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Throws_On_Vector_Count_Mismatch()
    {
        var inner = new StubEmbeddingGenerator(inputs =>
            [new Embedding<float>(new float[_dimensions])]); // Returns 1 embedding for 2 inputs

        var batcher = new BatchingEmbeddingGenerator(inner, batchSize: 4, dimensions: _dimensions);

        var ex = await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            batcher.GenerateAsync(["test1", "test2"]));

        Assert.Contains("unexpected vector count", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Normalizes_Vectors_To_Unit_L2_Length()
    {
        var inner = new StubEmbeddingGenerator(inputs =>
            [new Embedding<float>(_vector34)]); // norm = 5

        var batcher = new BatchingEmbeddingGenerator(inner, batchSize: 4, dimensions: _dimensions);
        var result = await batcher.GenerateAsync(["test"]);

        var vec = result[0].Vector.ToArray();
        Assert.Equal(0.6f, vec[0], precision: 5);
        Assert.Equal(0.8f, vec[1], precision: 5);
        Assert.Equal(0f, vec[2]);
        Assert.Equal(0f, vec[3]);

        var norm = Math.Sqrt(vec.Sum(x => x * x));
        Assert.True(Math.Abs(norm - 1.0) < 1e-5);
    }

    [Fact]
    public async Task Zero_Vector_Remains_Zero_Without_NaN()
    {
        var inner = new StubEmbeddingGenerator(inputs =>
            [new Embedding<float>(_zeroVector)]);

        var batcher = new BatchingEmbeddingGenerator(inner, batchSize: 4, dimensions: _dimensions);
        var result = await batcher.GenerateAsync(["test"]);

        var vec = result[0].Vector.ToArray();
        foreach (var val in vec)
        {
            Assert.False(float.IsNaN(val));
            Assert.Equal(0f, val);
        }
    }

    [Fact]
    public async Task Pauses_Between_Batches_When_Configured()
    {
        var timeProvider = new TrackingTimeProvider();
        var inner = new StubEmbeddingGenerator(inputs =>
            inputs.Select(_ => new Embedding<float>(new float[_dimensions])).ToList());

        var batcher = new BatchingEmbeddingGenerator(
            inner,
            batchSize: 2,
            dimensions: _dimensions,
            pauseBetweenBatchesMs: 500,
            timeProvider: timeProvider);

        var result = await batcher.GenerateAsync(_fourStrings);

        Assert.Equal(4, result.Count);
        Assert.Single(timeProvider.RecordedDelays);
        Assert.Equal(TimeSpan.FromMilliseconds(500), timeProvider.RecordedDelays[0]);
    }

    [Fact]
    public async Task Propagates_Cancellation_Immediately()
    {
        var inner = new StubEmbeddingGenerator(inputs =>
            inputs.Select(_ => new Embedding<float>(new float[_dimensions])).ToList());

        var batcher = new BatchingEmbeddingGenerator(inner, batchSize: 4, dimensions: _dimensions);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            batcher.GenerateAsync(["test"], cancellationToken: cts.Token));
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

    private sealed class TrackingTimeProvider : TimeProvider
    {
        public List<TimeSpan> RecordedDelays { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            RecordedDelays.Add(dueTime);
            callback(state);
            return new NoOpTimer();
        }

        private sealed class NoOpTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
