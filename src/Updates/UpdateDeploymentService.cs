using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace MusicDecrypto.Backend;

internal sealed class UpdateDeploymentService(
    IOptions<AppOptions> options,
    IWebHostEnvironment environment,
    IHostApplicationLifetime lifetime,
    ILogger<UpdateDeploymentService> logger)
{
    private int _isRunning;
    private string? _currentLogPath;

    public UpdateDeploymentResult SchedulePublishAndRestart()
    {
        var appRoot = environment.ContentRootPath;
        var manageScript = Path.Combine(appRoot, "scripts", "manage.sh");
        if (!File.Exists(manageScript))
        {
            return new UpdateDeploymentResult(false, $"Manage script not found: {manageScript}", null);
        }

        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            return new UpdateDeploymentResult(true, "publish-restart-already-running", _currentLogPath);
        }

        var paths = AppPaths.From(options.Value, appRoot);
        var logDirectory = Path.Combine(paths.Updates, "deployments");
        Directory.CreateDirectory(logDirectory);

        var logPath = Path.Combine(logDirectory, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-publish-restart.log");
        _currentLogPath = logPath;

        _ = Task.Run(() => PublishAndRestartAsync(appRoot, manageScript, logPath));
        return new UpdateDeploymentResult(true, "publish-restart-scheduled", logPath);
    }

    private async Task PublishAndRestartAsync(string appRoot, string manageScript, string logPath)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            await AppendLogAsync(logPath, $"Starting publish from {appRoot}.");

            var publishDir = Environment.GetEnvironmentVariable("MUSICDECRYPTO_MANAGE_PUBLISH_DIR");
            if (string.IsNullOrWhiteSpace(publishDir))
            {
                publishDir = Path.Combine(appRoot, "publish");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/env",
                WorkingDirectory = appRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("bash");
            startInfo.ArgumentList.Add(manageScript);
            startInfo.ArgumentList.Add("publish");
            startInfo.Environment["APP_DIR"] = appRoot;
            startInfo.Environment["PUBLISH_DIR"] = publishDir;

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                await AppendLogAsync(logPath, "Failed to start publish process.");
                Interlocked.Exchange(ref _isRunning, 0);
                return;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                await AppendLogAsync(logPath, "STDOUT:");
                await AppendLogAsync(logPath, stdout.TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                await AppendLogAsync(logPath, "STDERR:");
                await AppendLogAsync(logPath, stderr.TrimEnd());
            }

            if (process.ExitCode != 0)
            {
                await AppendLogAsync(logPath, $"Publish failed with exit code {process.ExitCode}. Service restart was not requested.");
                logger.LogError("Update publish failed with exit code {ExitCode}. LogPath={LogPath}", process.ExitCode, logPath);
                Interlocked.Exchange(ref _isRunning, 0);
                return;
            }

            await AppendLogAsync(logPath, "Publish completed. Stopping application; systemd Restart=always should start the updated service.");
            logger.LogInformation("Update publish completed. Stopping application for service restart. LogPath={LogPath}", logPath);
            lifetime.StopApplication();
        }
        catch (Exception ex)
        {
            await AppendLogAsync(logPath, $"Deployment failed: {ex}");
            logger.LogError(ex, "Update deployment failed. LogPath={LogPath}", logPath);
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    private static async Task AppendLogAsync(string logPath, string message)
    {
        await File.AppendAllTextAsync(
            logPath,
            $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}");
    }
}
