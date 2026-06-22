using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace MusicDecrypto.Backend;

internal sealed class UpdateDeploymentService(
    IOptions<AppOptions> options,
    IWebHostEnvironment environment,
    IHostApplicationLifetime lifetime,
    ILogger<UpdateDeploymentService> logger)
{
    private static readonly IReadOnlyDictionary<string, string> ManageEnvironmentAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MUSICDECRYPTO_MANAGE_BIND_HOST"] = "BIND_HOST",
            ["MUSICDECRYPTO_MANAGE_PORT"] = "PORT",
            ["MUSICDECRYPTO_MANAGE_APP_DIR"] = "APP_DIR",
            ["MUSICDECRYPTO_MANAGE_PUBLISH_DIR"] = "PUBLISH_DIR",
            ["MUSICDECRYPTO_MANAGE_DATA_DIR"] = "DATA_DIR",
            ["MUSICDECRYPTO_MANAGE_PACKAGE_DIR"] = "PACKAGE_DIR",
            ["MUSICDECRYPTO_MANAGE_FRONTEND_SOURCE_DIR"] = "FRONTEND_SOURCE_DIR",
            ["MUSICDECRYPTO_MANAGE_FRONTEND_DIR"] = "FRONTEND_DIR",
            ["MUSICDECRYPTO_MANAGE_SERVER_NAME"] = "SERVER_NAME",
            ["MUSICDECRYPTO_MANAGE_SSL_CERTIFICATE"] = "SSL_CERTIFICATE",
            ["MUSICDECRYPTO_MANAGE_SSL_CERTIFICATE_KEY"] = "SSL_CERTIFICATE_KEY",
            ["MUSICDECRYPTO_MANAGE_NGINX_SITE_FILE"] = "NGINX_SITE_FILE",
            ["MUSICDECRYPTO_MANAGE_NGINX_USER"] = "NGINX_USER",
            ["MUSICDECRYPTO_MANAGE_FRONTEND_BUILD"] = "FRONTEND_BUILD",
            ["MUSICDECRYPTO_MANAGE_FRONTEND_BUILD_USER"] = "FRONTEND_BUILD_USER",
            ["MUSICDECRYPTO_MANAGE_NODE_BIN"] = "NODE_BIN",
            ["MUSICDECRYPTO_MANAGE_PNPM_BIN"] = "PNPM_BIN"
        };

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
        ConfigureManageEnvironment(startInfo, appRoot, publishDir);

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

    private static void ConfigureManageEnvironment(ProcessStartInfo startInfo, string appRoot, string publishDir)
    {
        foreach (var (sourceKey, targetKey) in ManageEnvironmentAliases)
        {
            var value = Environment.GetEnvironmentVariable(sourceKey);
            if (!string.IsNullOrWhiteSpace(value))
            {
                startInfo.Environment[targetKey] = value;
            }
        }

        startInfo.Environment["APP_DIR"] = appRoot;
        startInfo.Environment["PUBLISH_DIR"] = publishDir;
    }

    private static async Task AppendLogAsync(string logPath, string message)
    {
        await File.AppendAllTextAsync(
            logPath,
            $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}");
    }
}
