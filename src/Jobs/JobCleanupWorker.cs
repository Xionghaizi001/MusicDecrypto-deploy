using Microsoft.Extensions.Options;

namespace MusicDecrypto.Backend;

internal sealed class JobCleanupWorker(
    JobStore jobs,
    JobDeletionService deletion,
    IOptionsMonitor<AppOptions> options,
    ILogger<JobCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CleanupOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CleanupOnceAsync(stoppingToken);
        }
    }

    private async Task CleanupOnceAsync(CancellationToken cancellationToken)
    {
        var retentionDays = options.CurrentValue.AutoDeleteAfterDays;
        if (retentionDays <= 0)
        {
            logger.LogDebug("Automatic job cleanup is disabled. AutoDeleteAfterDays={AutoDeleteAfterDays}", retentionDays);
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var expiredJobs = jobs.GetExpiredForDeletion(cutoff);
        foreach (var job in expiredJobs)
        {
            try
            {
                var result = deletion.DeleteFiles(job);
                await jobs.RemoveAsync(job.Id, cancellationToken);
                logger.LogInformation(
                    "Automatically deleted expired job {JobId}. RetentionDays={RetentionDays}, DeletedCount={DeletedCount}",
                    job.Id,
                    retentionDays,
                    result.DeletedPaths.Count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Failed to automatically delete expired job {JobId}", job.Id);
            }
        }
    }
}
