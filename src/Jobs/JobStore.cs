using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace MusicDecrypto.Backend;

internal sealed class JobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ConcurrentDictionary<string, JobRecord> _jobs = new();
    private readonly string _statePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JobStore(IOptions<AppOptions> options, IWebHostEnvironment environment)
    {
        var paths = AppPaths.From(options.Value, environment.ContentRootPath);
        Directory.CreateDirectory(paths.State);
        _statePath = Path.Combine(paths.State, "jobs.json");
        Load();
    }

    public IReadOnlyCollection<JobRecord> GetAll()
    {
        return _jobs.Values.OrderByDescending(job => job.CreatedAt).ToArray();
    }

    public JobRecord? Get(string id)
    {
        return _jobs.TryGetValue(id, out var job) ? job : null;
    }

    public IReadOnlyCollection<JobRecord> GetPending()
    {
        return _jobs.Values
            .Where(job => job.Status is JobStatus.Queued or JobStatus.Running)
            .OrderBy(job => job.CreatedAt)
            .ToArray();
    }

    public Task UpsertAsync(JobRecord job, CancellationToken cancellationToken)
    {
        _jobs[job.Id] = job;
        return PersistAsync(cancellationToken);
    }

    public Task MarkQueuedAsync(string id, string log, CancellationToken cancellationToken)
    {
        return UpdateAsync(id, job => job with
        {
            Status = JobStatus.Queued,
            StartedAt = null,
            CompletedAt = null,
            UpdatedAt = DateTimeOffset.UtcNow,
            Error = null,
            Log = log
        }, cancellationToken);
    }

    public Task MarkRunningAsync(string id, CancellationToken cancellationToken)
    {
        return UpdateAsync(id, job => job with
        {
            Status = JobStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Error = null
        }, cancellationToken);
    }

    public Task MarkCompletedAsync(string id, string outputPath, string log, CancellationToken cancellationToken)
    {
        return UpdateAsync(id, job => job with
        {
            Status = JobStatus.Completed,
            OutputPath = outputPath,
            Log = log,
            CompletedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Error = null
        }, cancellationToken);
    }

    public Task MarkFailedAsync(string id, string error, CancellationToken cancellationToken)
    {
        return UpdateAsync(id, job => job with
        {
            Status = JobStatus.Failed,
            Error = error,
            CompletedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private async Task UpdateAsync(string id, Func<JobRecord, JobRecord> update, CancellationToken cancellationToken)
    {
        if (_jobs.TryGetValue(id, out var current))
        {
            _jobs[id] = update(current);
            await PersistAsync(cancellationToken);
        }
    }

    private void Load()
    {
        if (!File.Exists(_statePath))
        {
            return;
        }

        var records = JsonSerializer.Deserialize<List<JobRecord>>(File.ReadAllText(_statePath), JsonOptions);
        if (records is null)
        {
            return;
        }

        foreach (var record in records)
        {
            _jobs[record.Id] = record;
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var records = GetAll();
            var json = JsonSerializer.Serialize(records, JsonOptions);
            var tempPath = $"{_statePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(tempPath, json, cancellationToken);
                File.Move(tempPath, _statePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
