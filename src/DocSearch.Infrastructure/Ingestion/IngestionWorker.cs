using DocSearch.Application.Abstractions;
using DocSearch.Application.Options;
using DocSearch.Domain;
using DocSearch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocSearch.Infrastructure.Ingestion;

public sealed class IngestionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IIngestionQueue _queue;
    private readonly IOptions<IngestionOptions> _options;
    private readonly ILogger<IngestionWorker> _logger;

    public IngestionWorker(
        IServiceScopeFactory scopeFactory,
        IIngestionQueue queue,
        IOptions<IngestionOptions> options,
        ILogger<IngestionWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IngestionWorker starting with {Concurrency} concurrent consumer(s).",
            _options.Value.MaxConcurrentDocuments);

        var maxConcurrency = Math.Max(1, _options.Value.MaxConcurrentDocuments);
        var consumerTasks = new List<Task>();

        // Start consumer tasks BEFORE or CONCURRENTLY with the startup recovery producer
        // to prevent deadlock when pending documents exceed QueueCapacity.
        for (var i = 0; i < maxConcurrency; i++)
        {
            consumerTasks.Add(Task.Run(() => ConsumeQueueAsync(stoppingToken), stoppingToken));
        }

        var recoveryTask = Task.Run(() => RecoverAndEnqueueStartupDocumentsAsync(stoppingToken), stoppingToken);

        await Task.WhenAll(consumerTasks.Concat([recoveryTask])).ConfigureAwait(false);

        _logger.LogInformation("IngestionWorker background service finished.");
    }

    private async Task ConsumeQueueAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var documentId in _queue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var ingestionService = scope.ServiceProvider.GetRequiredService<IIngestionService>();
                    await ingestionService.ProcessDocumentAsync(documentId, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Consumer cancelled while processing document {DocumentId}.", documentId);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error processing document {DocumentId} in consumer loop.", documentId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }

    private async Task RecoverAndEnqueueStartupDocumentsAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // 1. Reset any stuck Processing documents -> Uploaded
            var stuckDocs = await dbContext.Documents
                .Where(d => d.Status == DocumentStatus.Processing)
                .ToListAsync(stoppingToken)
                .ConfigureAwait(false);

            if (stuckDocs.Count > 0)
            {
                foreach (var doc in stuckDocs)
                {
                    doc.Status = DocumentStatus.Uploaded;
                    doc.ErrorMessage = null;
                }

                await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("Startup recovery: Reset {Count} stuck Processing document(s) to Uploaded.", stuckDocs.Count);
            }

            // 2. Enqueue all Uploaded documents in order of CreatedAt
            var pendingIds = await dbContext.Documents
                .Where(d => d.Status == DocumentStatus.Uploaded)
                .OrderBy(d => d.CreatedAt)
                .Select(d => d.Id)
                .ToListAsync(stoppingToken)
                .ConfigureAwait(false);

            if (pendingIds.Count > 0)
            {
                _logger.LogInformation("Startup recovery: Enqueueing {Count} pending Uploaded document(s).", pendingIds.Count);

                foreach (var docId in pendingIds)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await _queue.EnqueueAsync(docId, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown during startup recovery
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during startup document recovery.");
        }
    }
}
