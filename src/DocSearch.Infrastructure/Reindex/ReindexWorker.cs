using DocSearch.Application.Abstractions;
using DocSearch.Domain;
using DocSearch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocSearch.Infrastructure.Reindex;

public sealed class ReindexWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReindexQueue _queue;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReindexWorker> _logger;

    public ReindexWorker(
        IServiceScopeFactory scopeFactory,
        IReindexQueue queue,
        IConfiguration configuration,
        ILogger<ReindexWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ReindexWorker starting.");

        var consumerTask = Task.Run(() => ConsumeQueueAsync(stoppingToken), stoppingToken);
        var recoveryTask = Task.Run(() => RecoverAndEnqueueStartupJobsAsync(stoppingToken), stoppingToken);

        await Task.WhenAll(consumerTask, recoveryTask).ConfigureAwait(false);

        _logger.LogInformation("ReindexWorker background service finished.");
    }

    private async Task ConsumeQueueAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var jobId in _queue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var reindexService = scope.ServiceProvider.GetRequiredService<IReindexService>();
                    await reindexService.ProcessJobAsync(jobId, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Reindex consumer cancelled while processing job {JobId}.", jobId);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error processing reindex job {JobId} in consumer loop.", jobId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }

    private async Task RecoverAndEnqueueStartupJobsAsync(CancellationToken stoppingToken)
    {
        if (_configuration.GetValue<bool>("SkipStartupDbSeeding"))
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (!await dbContext.Database.CanConnectAsync(stoppingToken).ConfigureAwait(false))
            {
                _logger.LogWarning("ReindexWorker startup recovery skipped because database is not reachable.");
                return;
            }

            // 1. Reset any stuck Running jobs -> Queued
            var stuckJobs = await dbContext.ReindexJobs
                .Where(j => j.Status == ReindexJobStatus.Running)
                .ToListAsync(stoppingToken)
                .ConfigureAwait(false);

            if (stuckJobs.Count > 0)
            {
                foreach (var job in stuckJobs)
                {
                    job.Status = ReindexJobStatus.Queued;
                    job.ErrorMessage = null;
                }

                await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("Reindex startup recovery: Reset {Count} stuck Running job(s) to Queued.", stuckJobs.Count);
            }

            // 2. Enqueue all Queued jobs in order of CreatedAt
            var pendingJobIds = await dbContext.ReindexJobs
                .Where(j => j.Status == ReindexJobStatus.Queued)
                .OrderBy(j => j.CreatedAt)
                .Select(j => j.Id)
                .ToListAsync(stoppingToken)
                .ConfigureAwait(false);

            if (pendingJobIds.Count > 0)
            {
                _logger.LogInformation("Reindex startup recovery: Enqueueing {Count} pending Queued job(s).", pendingJobIds.Count);

                foreach (var jobId in pendingJobIds)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await _queue.EnqueueAsync(jobId, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown during startup recovery
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during startup reindex job recovery.");
        }
    }
}
