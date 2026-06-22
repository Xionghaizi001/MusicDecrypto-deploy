using Microsoft.Extensions.Options;

namespace MusicDecrypto.Backend;

internal sealed class JobRuntimeLogService(
    IOptions<AppOptions> options,
    IWebHostEnvironment environment)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task WriteAsync(
        string jobId,
        string title,
        string detail,
        CancellationToken cancellationToken)
    {
        var paths = AppPaths.From(options.Value, environment.ContentRootPath);
        Directory.CreateDirectory(paths.Logs);

        var logPath = Path.Combine(paths.Logs, "job-runtime.log");
        var entry = string.Join(
            Environment.NewLine,
            $"[{DateTimeOffset.UtcNow:O}] JobId={jobId} {title}",
            detail.Trim(),
            string.Empty,
            new string('-', 80),
            string.Empty);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(logPath, entry, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }
}
