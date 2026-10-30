using System.Threading.Channels;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Options;
using Microsoft.Extensions.Options;

namespace DocSearch.Infrastructure.Ingestion;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Required by CONVENTIONS.md core contracts")]
public sealed class IngestionQueue : IIngestionQueue
{
    private readonly Channel<Guid> _channel;

    public IngestionQueue(IOptions<IngestionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var capacity = options.Value.QueueCapacity > 0 ? options.Value.QueueCapacity : 100;
        var channelOptions = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };

        _channel = Channel.CreateBounded<Guid>(channelOptions);
    }

    public ValueTask EnqueueAsync(Guid documentId, CancellationToken ct)
    {
        return _channel.Writer.WriteAsync(documentId, ct);
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
