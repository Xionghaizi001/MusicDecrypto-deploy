using System.Diagnostics;
using System.Text;
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
        await RepairCompletedJobsAsync(stoppingToken);
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

    private async Task RepairCompletedJobsAsync(CancellationToken cancellationToken)
    {
        var paths = AppPaths.From(options.Value, environment.ContentRootPath);

        foreach (var job in jobs.GetFailed())
        {
            if (!string.Equals(job.Error, "Decrypto finished but no output file was produced.", StringComparison.Ordinal))
            {
                continue;
            }

            var outputDirectory = Path.Combine(paths.Outputs, job.Id);
            var outputFile = FindExistingOutputFile(job, outputDirectory);
            if (outputFile is null)
            {
                logger.LogInformation(
                    "Failed job {JobId} was checked for existing output, but no recoverable file was found. OutputDirectory={OutputDirectory}, InputDirectory={InputDirectory}",
                    job.Id,
                    outputDirectory,
                    Path.GetDirectoryName(job.InputPath));
                continue;
            }

            var log = TrimLog(
                job.Log ?? string.Empty,
                $"Recovered existing output file on service startup: {outputFile.Path}",
                $"OutputSource: {outputFile.Source}");

            await jobs.MarkCompletedAsync(job.Id, outputFile.Path, log, cancellationToken);
            logger.LogInformation(
                "Recovered failed job {JobId} with existing output file. OutputPath={OutputPath}, OutputSource={OutputSource}, OutputBytes={OutputBytes}",
                job.Id,
                outputFile.Path,
                outputFile.Source,
                outputFile.Size);
        }
    }

    private async Task RequeuePendingJobsAsync(CancellationToken cancellationToken)
    {
        foreach (var job in jobs.GetPending())
        {
            if (!File.Exists(job.InputPath))
            {
                var error = $"Input file no longer exists: {job.InputPath}";
                logger.LogWarning("Pending job {JobId} cannot be requeued: {Error}", job.Id, error);
                await jobs.MarkFailedAsync(job.Id, error, cancellationToken);
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
        logger.LogInformation(
            "Started job {JobId}. OriginalFileName={OriginalFileName}, InputPath={InputPath}, InputExists={InputExists}, InputBytes={InputBytes}",
            jobId,
            job.OriginalFileName,
            job.InputPath,
            File.Exists(job.InputPath),
            GetFileSize(job.InputPath));

        var executablePath = Path.GetFullPath(options.Value.DecryptoExecutablePath, environment.ContentRootPath);
        if (!File.Exists(executablePath))
        {
            var error = $"Decrypto executable not found: {executablePath}";
            logger.LogError("Job {JobId} failed before process start: {Error}", jobId, error);
            await jobs.MarkFailedAsync(jobId, error, cancellationToken, BuildPreflightLog(job, executablePath, null, error));
            return;
        }

        if (!File.Exists(job.InputPath))
        {
            var error = $"Input file not found before decrypto start: {job.InputPath}";
            logger.LogError("Job {JobId} failed before process start: {Error}", jobId, error);
            await jobs.MarkFailedAsync(jobId, error, cancellationToken, BuildPreflightLog(job, executablePath, null, error));
            return;
        }

        var outputDirectory = Path.Combine(paths.Outputs, jobId);
        Directory.CreateDirectory(outputDirectory);
        var cliOutputArgument = Path.Combine(outputDirectory, "_output");
        Directory.CreateDirectory(cliOutputArgument);

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
        startInfo.ArgumentList.Add(cliOutputArgument);
        if (options.Value.ForceOverwrite)
        {
            startInfo.ArgumentList.Add("--force-overwrite");
        }
        if (options.Value.ExtensiveDetection)
        {
            startInfo.ArgumentList.Add("--extensive");
        }

        var commandLine = FormatCommand(startInfo);
        logger.LogInformation(
            "Starting decrypto process for job {JobId}. Command={Command}, WorkingDirectory={WorkingDirectory}, OutputDirectory={OutputDirectory}",
            jobId,
            commandLine,
            startInfo.WorkingDirectory,
            outputDirectory);

        var result = await RunProcessAsync(startInfo, cancellationToken);
        if (!result.Started)
        {
            var error = $"Failed to start decrypto process: {result.StartError ?? "unknown start failure"}";
            logger.LogError(
                "Job {JobId} failed to start decrypto process. Command={Command}, WorkingDirectory={WorkingDirectory}, Error={Error}",
                jobId,
                commandLine,
                startInfo.WorkingDirectory,
                result.StartError);
            await jobs.MarkFailedAsync(jobId, error, cancellationToken, BuildProcessLog(job, startInfo, result));
            return;
        }

        if (result.ExitCode != 0)
        {
            var error = $"Decrypto exited with code {result.ExitCode}.";
            logger.LogError(
                "Job {JobId} failed. ExitCode={ExitCode}, DurationMs={DurationMs}, Command={Command}, Stdout={Stdout}, Stderr={Stderr}",
                jobId,
                result.ExitCode,
                result.Duration.TotalMilliseconds,
                commandLine,
                TrimLog(result.Stdout),
                TrimLog(result.Stderr));
            await jobs.MarkFailedAsync(jobId, error, cancellationToken, BuildProcessLog(job, startInfo, result));
            return;
        }

        logger.LogInformation(
            "Decrypto process finished for job {JobId}. ExitCode={ExitCode}, DurationMs={DurationMs}, StdoutBytes={StdoutBytes}, StderrBytes={StderrBytes}",
            jobId,
            result.ExitCode,
            result.Duration.TotalMilliseconds,
            Encoding.UTF8.GetByteCount(result.Stdout),
            Encoding.UTF8.GetByteCount(result.Stderr));

        var outputFile = FindOutputFile(job, outputDirectory, result.StartedAtUtc);

        if (outputFile is null)
        {
            var error = "Decrypto finished but no output file was produced.";
            logger.LogError(
                "Job {JobId} failed after successful process exit: {Error} OutputDirectory={OutputDirectory}, InputDirectory={InputDirectory}",
                jobId,
                error,
                outputDirectory,
                Path.GetDirectoryName(job.InputPath));
            await jobs.MarkFailedAsync(jobId, error, cancellationToken, BuildProcessLog(job, startInfo, result));
            return;
        }

        logger.LogInformation(
            "Job {JobId} completed. OutputPath={OutputPath}, OutputSource={OutputSource}, OutputBytes={OutputBytes}",
            jobId,
            outputFile.Path,
            outputFile.Source,
            outputFile.Size);

        await jobs.MarkCompletedAsync(jobId, outputFile.Path, BuildProcessLog(job, startInfo, result), cancellationToken);
    }

    private static async Task<ProcessResult> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        using var process = TryStartProcess(startInfo, out var startError);
        if (process is null)
        {
            stopwatch.Stop();
            return new ProcessResult(false, -1, string.Empty, string.Empty, stopwatch.Elapsed, startedAtUtc, startError);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            stopwatch.Stop();
            return new ProcessResult(true, process.ExitCode, stdout, stderr, stopwatch.Elapsed, startedAtUtc, null);
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

    private static Process? TryStartProcess(ProcessStartInfo startInfo, out string? startError)
    {
        try
        {
            startError = null;
            return Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            startError = ex.Message;
            return null;
        }
    }

    private static string TrimLog(params string[] values)
    {
        var combined = string.Join(Environment.NewLine, values.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        return combined.Length <= 4000 ? combined : combined[..4000];
    }

    private static long? GetFileSize(string path)
    {
        return File.Exists(path) ? new FileInfo(path).Length : null;
    }

    private static OutputCandidate? FindOutputFile(JobRecord job, string outputDirectory, DateTimeOffset processStartedAtUtc)
    {
        var outputCandidates = EnumerateOutputDirectoryCandidates(job.InputPath, outputDirectory).ToArray();
        if (outputCandidates.Length > 0)
        {
            return outputCandidates
                .OrderByDescending(candidate => candidate.LastWriteUtc)
                .First();
        }

        return EnumerateInputDirectoryCandidates(job, processStartedAtUtc)
            .Concat(EnumerateOutputParentDirectoryCandidates(job, outputDirectory, processStartedAtUtc))
            .Concat(EnumerateInputParentDirectoryCandidates(job, processStartedAtUtc))
            .OrderByDescending(candidate => candidate.Name.StartsWith($"{job.Id}-", StringComparison.Ordinal))
            .ThenByDescending(candidate => candidate.LastWriteUtc)
            .FirstOrDefault();
    }

    private static OutputCandidate? FindExistingOutputFile(JobRecord job, string outputDirectory)
    {
        var outputCandidates = EnumerateOutputDirectoryCandidates(job.InputPath, outputDirectory).ToArray();
        if (outputCandidates.Length > 0)
        {
            return outputCandidates
                .OrderByDescending(candidate => candidate.LastWriteUtc)
                .First();
        }

        return EnumerateJobPrefixedInputDirectoryCandidates(job)
            .Concat(EnumerateJobPrefixedOutputParentDirectoryCandidates(job, outputDirectory))
            .Concat(EnumerateJobPrefixedInputParentDirectoryCandidates(job))
            .OrderByDescending(candidate => candidate.LastWriteUtc)
            .FirstOrDefault();
    }

    private static IEnumerable<OutputCandidate> EnumerateOutputDirectoryCandidates(string inputPath, string outputDirectory)
    {
        if (!Directory.Exists(outputDirectory))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories))
        {
            if (PathsEqual(path, inputPath))
            {
                continue;
            }

            yield return CreateOutputCandidate(path, "output-directory");
        }
    }

    private static IEnumerable<OutputCandidate> EnumerateInputDirectoryCandidates(JobRecord job, DateTimeOffset processStartedAtUtc)
    {
        var inputDirectory = Path.GetDirectoryName(job.InputPath);
        if (string.IsNullOrWhiteSpace(inputDirectory) || !Directory.Exists(inputDirectory))
        {
            yield break;
        }

        var earliestWriteUtc = processStartedAtUtc.UtcDateTime.AddSeconds(-5);
        var recentCandidates = Directory
            .EnumerateFiles(inputDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !PathsEqual(path, job.InputPath))
            .Select(path => CreateOutputCandidate(path, "input-directory"))
            .Where(candidate => candidate.LastWriteUtc >= earliestWriteUtc)
            .ToArray();

        var jobPrefixed = recentCandidates
            .Where(candidate => candidate.Name.StartsWith($"{job.Id}-", StringComparison.Ordinal))
            .ToArray();

        foreach (var candidate in jobPrefixed.Length > 0 ? jobPrefixed : recentCandidates)
        {
            yield return candidate;
        }
    }

    private static IEnumerable<OutputCandidate> EnumerateInputParentDirectoryCandidates(
        JobRecord job,
        DateTimeOffset processStartedAtUtc)
    {
        var parentDirectory = GetInputParentDirectory(job.InputPath);
        if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
        {
            yield break;
        }

        var earliestWriteUtc = processStartedAtUtc.UtcDateTime.AddSeconds(-5);
        var candidates = Directory
            .EnumerateFiles(parentDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !PathsEqual(path, job.InputPath))
            .Select(path => CreateOutputCandidate(path, "input-parent-directory"))
            .Where(candidate => candidate.LastWriteUtc >= earliestWriteUtc)
            .ToArray();

        var jobPrefixed = candidates
            .Where(candidate => candidate.Name.StartsWith($"{job.Id}-", StringComparison.Ordinal))
            .ToArray();

        foreach (var candidate in jobPrefixed.Length > 0 ? jobPrefixed : candidates)
        {
            yield return candidate;
        }
    }

    private static IEnumerable<OutputCandidate> EnumerateOutputParentDirectoryCandidates(
        JobRecord job,
        string outputDirectory,
        DateTimeOffset processStartedAtUtc)
    {
        var parentDirectory = Path.GetDirectoryName(Path.GetFullPath(outputDirectory));
        if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
        {
            yield break;
        }

        var earliestWriteUtc = processStartedAtUtc.UtcDateTime.AddSeconds(-5);
        var candidates = Directory
            .EnumerateFiles(parentDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !PathsEqual(path, job.InputPath))
            .Select(path => CreateOutputCandidate(path, "output-parent-directory"))
            .Where(candidate => candidate.LastWriteUtc >= earliestWriteUtc)
            .ToArray();

        var jobPrefixed = candidates
            .Where(candidate => candidate.Name.StartsWith($"{job.Id}-", StringComparison.Ordinal))
            .ToArray();

        foreach (var candidate in jobPrefixed.Length > 0 ? jobPrefixed : candidates)
        {
            yield return candidate;
        }
    }

    private static IEnumerable<OutputCandidate> EnumerateJobPrefixedInputDirectoryCandidates(JobRecord job)
    {
        var inputDirectory = Path.GetDirectoryName(job.InputPath);
        if (string.IsNullOrWhiteSpace(inputDirectory) || !Directory.Exists(inputDirectory))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(inputDirectory, $"{job.Id}-*", SearchOption.TopDirectoryOnly))
        {
            if (PathsEqual(path, job.InputPath))
            {
                continue;
            }

            yield return CreateOutputCandidate(path, "input-directory");
        }
    }

    private static IEnumerable<OutputCandidate> EnumerateJobPrefixedInputParentDirectoryCandidates(JobRecord job)
    {
        var parentDirectory = GetInputParentDirectory(job.InputPath);
        if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(parentDirectory, $"{job.Id}-*", SearchOption.TopDirectoryOnly))
        {
            if (PathsEqual(path, job.InputPath))
            {
                continue;
            }

            yield return CreateOutputCandidate(path, "input-parent-directory");
        }
    }

    private static IEnumerable<OutputCandidate> EnumerateJobPrefixedOutputParentDirectoryCandidates(
        JobRecord job,
        string outputDirectory)
    {
        var parentDirectory = Path.GetDirectoryName(Path.GetFullPath(outputDirectory));
        if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(parentDirectory, $"{job.Id}-*", SearchOption.TopDirectoryOnly))
        {
            yield return CreateOutputCandidate(path, "output-parent-directory");
        }
    }

    private static OutputCandidate CreateOutputCandidate(string path, string source)
    {
        var file = new FileInfo(path);
        return new OutputCandidate(
            Path.GetFullPath(path),
            Path.GetFileName(path),
            source,
            file.LastWriteTimeUtc,
            file.Length);
    }

    private static bool PathsEqual(string first, string second)
    {
        return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.Ordinal);
    }

    private static string? GetInputParentDirectory(string inputPath)
    {
        var inputDirectory = Path.GetDirectoryName(inputPath);
        return string.IsNullOrWhiteSpace(inputDirectory)
            ? null
            : Path.GetDirectoryName(inputDirectory);
    }

    private static string BuildPreflightLog(JobRecord job, string executablePath, ProcessStartInfo? startInfo, string error)
    {
        var builder = new StringBuilder()
            .AppendLine($"JobId: {job.Id}")
            .AppendLine($"OriginalFileName: {job.OriginalFileName}")
            .AppendLine($"InputPath: {job.InputPath}")
            .AppendLine($"InputExists: {File.Exists(job.InputPath)}")
            .AppendLine($"InputBytes: {GetFileSize(job.InputPath)?.ToString() ?? "n/a"}")
            .AppendLine($"ExecutablePath: {executablePath}")
            .AppendLine($"ExecutableExists: {File.Exists(executablePath)}");

        if (startInfo is not null)
        {
            builder
                .AppendLine($"WorkingDirectory: {startInfo.WorkingDirectory}")
                .AppendLine($"Command: {FormatCommand(startInfo)}");
        }

        builder.AppendLine($"Error: {error}");
        return TrimLog(builder.ToString());
    }

    private static string BuildProcessLog(JobRecord job, ProcessStartInfo startInfo, ProcessResult result)
    {
        var cliOutputArgument = startInfo.ArgumentList.Count >= 3 ? startInfo.ArgumentList[2] : null;
        var outputDirectory = GetEffectiveOutputDirectory(cliOutputArgument);
        var processInputPath = startInfo.ArgumentList.Count >= 1 ? startInfo.ArgumentList[0] : null;
        var builder = new StringBuilder()
            .AppendLine($"JobId: {job.Id}")
            .AppendLine($"OriginalFileName: {job.OriginalFileName}")
            .AppendLine($"InputPath: {job.InputPath}")
            .AppendLine($"InputExists: {File.Exists(job.InputPath)}")
            .AppendLine($"InputBytes: {GetFileSize(job.InputPath)?.ToString() ?? "n/a"}")
            .AppendLine($"ProcessInputPath: {processInputPath ?? "n/a"}")
            .AppendLine($"ProcessInputExists: {(!string.IsNullOrWhiteSpace(processInputPath) && File.Exists(processInputPath))}")
            .AppendLine($"Command: {FormatCommand(startInfo)}")
            .AppendLine($"WorkingDirectory: {startInfo.WorkingDirectory}")
            .AppendLine($"CliOutputArgument: {cliOutputArgument ?? "n/a"}")
            .AppendLine($"OutputDirectory: {outputDirectory ?? "n/a"}")
            .AppendLine($"OutputDirectoryExists: {(!string.IsNullOrWhiteSpace(outputDirectory) && Directory.Exists(outputDirectory))}")
            .AppendLine($"Started: {result.Started}")
            .AppendLine($"ExitCode: {result.ExitCode}")
            .AppendLine($"DurationMs: {Math.Round(result.Duration.TotalMilliseconds)}")
            .AppendLine("OutputCandidates:")
            .AppendLine(FormatOutputCandidates(job, outputDirectory, result.StartedAtUtc));

        if (!string.IsNullOrWhiteSpace(result.StartError))
        {
            builder.AppendLine($"StartError: {result.StartError}");
        }

        if (!string.IsNullOrWhiteSpace(result.Stdout))
        {
            builder.AppendLine("STDOUT:").AppendLine(result.Stdout.Trim());
        }

        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            builder.AppendLine("STDERR:").AppendLine(result.Stderr.Trim());
        }

        return TrimLog(builder.ToString());
    }

    private static string FormatCommand(ProcessStartInfo startInfo)
    {
        return string.Join(
            " ",
            new[] { startInfo.FileName }.Concat(startInfo.ArgumentList).Select(QuoteArgument));
    }

    private static string QuoteArgument(string value)
    {
        return value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
    }

    private sealed record ProcessResult(
        bool Started,
        int ExitCode,
        string Stdout,
        string Stderr,
        TimeSpan Duration,
        DateTimeOffset StartedAtUtc,
        string? StartError);

    private static string FormatOutputCandidates(JobRecord job, string? outputDirectory, DateTimeOffset processStartedAtUtc)
    {
        var candidates = Enumerable.Empty<OutputCandidate>();
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            candidates = candidates.Concat(EnumerateOutputDirectoryCandidates(job.InputPath, outputDirectory));
            candidates = candidates.Concat(EnumerateOutputParentDirectoryCandidates(job, outputDirectory, processStartedAtUtc));
        }

        candidates = candidates.Concat(EnumerateInputDirectoryCandidates(job, processStartedAtUtc));
        candidates = candidates.Concat(EnumerateInputParentDirectoryCandidates(job, processStartedAtUtc));

        var lines = candidates
            .OrderBy(candidate => candidate.Source)
            .ThenByDescending(candidate => candidate.LastWriteUtc)
            .Take(20)
            .Select(candidate => $"- [{candidate.Source}] {candidate.Path} ({candidate.Size} bytes, {candidate.LastWriteUtc:O})")
            .ToArray();

        return lines.Length == 0 ? "- none" : string.Join(Environment.NewLine, lines);
    }

    private static string? GetEffectiveOutputDirectory(string? cliOutputArgument)
    {
        return string.IsNullOrWhiteSpace(cliOutputArgument)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(cliOutputArgument));
    }

    private sealed record OutputCandidate(
        string Path,
        string Name,
        string Source,
        DateTime LastWriteUtc,
        long Size);
}
