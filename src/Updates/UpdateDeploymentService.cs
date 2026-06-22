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

    public UpdateDeploymentResult Schedule(IReadOnlyCollection<string> targets)
    {
        var normalizedTargets = targets
            .Select(UpdatePackageService.NormalizeTarget)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedTargets.Length == 0)
        {
            return new UpdateDeploymentResult(false, "no-update-targets", null);
        }

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

        var logPath = Path.Combine(logDirectory, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-deploy.log");
        _currentLogPath = logPath;

        _ = Task.Run(() => DeployAsync(appRoot, manageScript, logPath, normalizedTargets));
        return new UpdateDeploymentResult(true, $"deploy-scheduled:{string.Join(",", normalizedTargets)}", logPath);
    }

    private async Task DeployAsync(string appRoot, string manageScript, string logPath, IReadOnlyCollection<string> targets)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            await AppendLogAsync(logPath, $"Starting deployment from {appRoot}. Targets: {string.Join(", ", targets)}.");

            var publishDir = Environment.GetEnvironmentVariable("MUSICDECRYPTO_MANAGE_PUBLISH_DIR");
            if (string.IsNullOrWhiteSpace(publishDir))
            {
                publishDir = Path.Combine(appRoot, "publish");
            }

            if (targets.Contains(UpdatePackageService.FrontendTarget, StringComparer.OrdinalIgnoreCase))
            {
                var frontendExitCode = await RunManageCommandAsync(
                    appRoot,
                    manageScript,
                    "publish-frontend",
                    publishDir,
                    logPath);
                if (frontendExitCode != 0)
                {
                    await AppendLogAsync(logPath, $"Frontend publish failed with exit code {frontendExitCode}. Service restart was not requested.");
                    logger.LogError("Update frontend publish failed with exit code {ExitCode}. LogPath={LogPath}", frontendExitCode, logPath);
                    Interlocked.Exchange(ref _isRunning, 0);
                    return;
                }
            }

            if (targets.Contains(UpdatePackageService.BackendTarget, StringComparer.OrdinalIgnoreCase))
            {
                var backendExitCode = await RunManageCommandAsync(
                    appRoot,
                    manageScript,
                    "publish",
                    publishDir,
                    logPath);
                if (backendExitCode != 0)
                {
                    await AppendLogAsync(logPath, $"Backend publish failed with exit code {backendExitCode}. Service restart was not requested.");
                    logger.LogError("Update backend publish failed with exit code {ExitCode}. LogPath={LogPath}", backendExitCode, logPath);
                    Interlocked.Exchange(ref _isRunning, 0);
                    return;
                }

                await AppendLogAsync(logPath, "Backend publish completed. Stopping application; systemd Restart=always should start the updated service.");
                logger.LogInformation("Update backend publish completed. Stopping application for service restart. LogPath={LogPath}", logPath);
                lifetime.StopApplication();
                return;
            }

            await AppendLogAsync(logPath, "Deployment completed without backend restart.");
            logger.LogInformation("Update deployment completed without backend restart. LogPath={LogPath}", logPath);
            Interlocked.Exchange(ref _isRunning, 0);
        }
        catch (Exception ex)
        {
            await AppendLogAsync(logPath, $"Deployment failed: {ex}");
            logger.LogError(ex, "Update deployment failed. LogPath={LogPath}", logPath);
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    private static async Task<int> RunManageCommandAsync(
        string appRoot,
        string manageScript,
        string command,
        string publishDir,
        string logPath)
    {
        await AppendLogAsync(logPath, $"Running scripts/manage.sh {command}.");

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
        startInfo.ArgumentList.Add(command);
        startInfo.Environment["APP_DIR"] = appRoot;
        startInfo.Environment["PUBLISH_DIR"] = publishDir;

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            await AppendLogAsync(logPath, $"Failed to start manage command: {command}.");
            return -1;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            await AppendLogAsync(logPath, $"STDOUT ({command}):");
            await AppendLogAsync(logPath, stdout.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            await AppendLogAsync(logPath, $"STDERR ({command}):");
            await AppendLogAsync(logPath, stderr.TrimEnd());
        }

        return process.ExitCode;
    }

    private static async Task AppendLogAsync(string logPath, string message)
    {
        await File.AppendAllTextAsync(
            logPath,
            $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}");
    }
}
