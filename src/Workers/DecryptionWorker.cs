using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace MusicDecrypto.Backend;

internal sealed class DecryptionWorker(
    JobQueue queue,
    JobStore jobs,
    IOptions<AppOptions> options,
    IWebHostEnvironment environment,
    ILogger<DecryptionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeuePendingJobsAsync(stoppingToken);

        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessJobAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error while processing job {JobId}", jobId);
                await jobs.MarkFailedAsync(jobId, ex.Message, CancellationToken.None);
            }
        }
    }

    private async Task RequeuePendingJobsAsync(CancellationToken cancellationToken)
    {
        foreach (var job in jobs.GetPending())
        {
            if (!File.Exists(job.InputPath))
            {
                await jobs.MarkFailedAsync(job.Id, $"Input file no longer exists: {job.InputPath}", cancellationToken);
                continue;
            }

            if (job.Status == JobStatus.Running)
            {
                await jobs.MarkQueuedAsync(
                    job.Id,
                    "Service restarted while job was running; job was queued for another attempt.",
                    cancellationToken);
            }

            await queue.EnqueueAsync(job.Id, cancellationToken);
            logger.LogInformation("Queued pending job {JobId} from persisted state", job.Id);
        }
    }

    private async Task ProcessJobAsync(string jobId, CancellationToken cancellationToken)
    {
        var job = jobs.Get(jobId);
        if (job is null)
        {
            logger.LogWarning("Job {JobId} disappeared before processing", jobId);
            return;
        }

        var paths = AppPaths.From(options.Value, environment.ContentRootPath);
        Directory.CreateDirectory(paths.Outputs);

        await jobs.MarkRunningAsync(jobId, cancellationToken);

        var executablePath = Path.GetFullPath(options.Value.DecryptoExecutablePath, environment.ContentRootPath);
        if (!File.Exists(executablePath))
        {
            await jobs.MarkFailedAsync(jobId, $"Decrypto executable not found: {executablePath}", cancellationToken);
            return;
        }

        var outputDirectory = Path.Combine(paths.Outputs, jobId);
        Directory.CreateDirectory(outputDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? environment.ContentRootPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(job.InputPath);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputDirectory);
        if (options.Value.ForceOverwrite)
        {
            startInfo.ArgumentList.Add("--force-overwrite");
        }
        if (options.Value.ExtensiveDetection)
        {
            startInfo.ArgumentList.Add("--extensive");
        }

        var result = await RunProcessAsync(startInfo, cancellationToken);
        if (!result.Started)
        {
            await jobs.MarkFailedAsync(jobId, "Failed to start decrypto process.", cancellationToken);
            return;
        }

        if (result.ExitCode != 0)
        {
            await jobs.MarkFailedAsync(jobId, TrimLog(result.Stderr, result.Stdout), cancellationToken);
            return;
        }

        var outputFile = Directory.EnumerateFiles(outputDirectory, "*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (outputFile is null)
        {
            await jobs.MarkFailedAsync(jobId, "Decrypto finished but no output file was produced.", cancellationToken);
            return;
        }

        await jobs.MarkCompletedAsync(jobId, outputFile, TrimLog(result.Stdout, result.Stderr), cancellationToken);
    }

    private static async Task<ProcessResult> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new ProcessResult(false, -1, string.Empty, string.Empty);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ProcessResult(true, process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
    }

    private static string TrimLog(params string[] values)
    {
        var combined = string.Join(Environment.NewLine, values.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        return combined.Length <= 4000 ? combined : combined[..4000];
    }

    private sealed record ProcessResult(bool Started, int ExitCode, string Stdout, string Stderr);
}
