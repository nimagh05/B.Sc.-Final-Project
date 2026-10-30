using DocSearch.Application.Options;
using DocSearch.Infrastructure.Ingestion;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocSearch.Infrastructure.Tests.Ingestion;

public sealed class IngestionQueueTests
{
    [Fact]
    public async Task EnqueueAndDequeue_PreservesFifoOrder()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new IngestionOptions
        {
            QueueCapacity = 10,
            MaxConcurrentDocuments = 1
        });

        var queue = new IngestionQueue(options);

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        await queue.EnqueueAsync(id1, CancellationToken.None);
        await queue.EnqueueAsync(id2, CancellationToken.None);
        await queue.EnqueueAsync(id3, CancellationToken.None);

        queue.Complete();

        var dequeued = new List<Guid>();
        await foreach (var item in queue.DequeueAllAsync(CancellationToken.None))
        {
            dequeued.Add(item);
        }

        Assert.Equal([id1, id2, id3], dequeued);
    }

    [Fact]
    public async Task BoundedChannel_WaitsWhenFull_AndResumesOnDequeue()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new IngestionOptions
        {
            QueueCapacity = 2,
            MaxConcurrentDocuments = 1
        });

        var queue = new IngestionQueue(options);

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        await queue.EnqueueAsync(id1, CancellationToken.None);
        await queue.EnqueueAsync(id2, CancellationToken.None);

        // Third item enqueue should wait because capacity is 2
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var enqueueTask = queue.EnqueueAsync(id3, cts.Token).AsTask();

        // Enqueue task should not be completed immediately because channel is full
        Assert.False(enqueueTask.IsCompleted);

        // Now drain one item
        using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var enumerator = queue.DequeueAllAsync(drainCts.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(id1, enumerator.Current);

        // Now the enqueueTask should complete
        await enqueueTask;
    }
}
