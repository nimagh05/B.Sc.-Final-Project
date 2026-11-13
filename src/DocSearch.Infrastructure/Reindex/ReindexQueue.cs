using System.Threading.Channels;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Options;
using Microsoft.Extensions.Options;

namespace DocSearch.Infrastructure.Reindex;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Matches IngestionQueue naming")]
public sealed class ReindexQueue : IReindexQueue
{
    private readonly Channel<Guid> _channel;

    public ReindexQueue(IOptions<IngestionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var capacity = options.Value.QueueCapacity > 0 ? options.Value.QueueCapacity : 100;
        var channelOptions = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };

        _channel = Channel.CreateBounded<Guid>(channelOptions);
    }

    public ValueTask EnqueueAsync(Guid jobId, CancellationToken ct)
    {
        return _channel.Writer.WriteAsync(jobId, ct);
    }

    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct)
    {
        return _channel.Reader.ReadAllAsync(ct);
    }

    public void Complete()
    {
        _channel.Writer.TryComplete();
    }
}
