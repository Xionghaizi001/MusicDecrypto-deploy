using Microsoft.Extensions.Options;

namespace MusicDecrypto.Backend;

internal sealed class JobDeletionService(
    IOptions<AppOptions> options,
    IWebHostEnvironment environment,
    ILogger<JobDeletionService> logger)
{
    public JobDeleteResult DeleteFiles(JobRecord job)
    {
        var paths = AppPaths.From(options.Value, environment.ContentRootPath);
        var deleted = new List<string>();
        var missing = new List<string>();

        DeleteFile(job.InputPath, deleted, missing);

        if (!string.IsNullOrWhiteSpace(job.OutputPath))
        {
            DeleteFile(job.OutputPath, deleted, missing);
        }

        DeleteDirectory(Path.Combine(paths.Outputs, job.Id), deleted, missing);
        DeleteJobPrefixedFiles(paths.Outputs, job.Id, deleted, missing);
        DeleteJobPrefixedFiles(paths.TusStore, job.TusFileId, deleted, missing);

        logger.LogInformation(
            "Deleted job files. JobId={JobId}, DeletedCount={DeletedCount}, MissingCount={MissingCount}",
            job.Id,
            deleted.Count,
            missing.Count);

        return new JobDeleteResult(job.Id, deleted, missing);
    }

    private static void DeleteJobPrefixedFiles(
        string directory,
        string prefix,
        List<string> deleted,
        List<string> missing)
    {
        if (!Directory.Exists(directory))
        {
            missing.Add(directory);
            return;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(directory, $"{prefix}*", SearchOption.TopDirectoryOnly))
        {
            if (File.Exists(path))
            {
                DeleteFile(path, deleted, missing);
            }
            else if (Directory.Exists(path))
            {
                DeleteDirectory(path, deleted, missing);
            }
        }
    }

    private static void DeleteFile(string path, List<string> deleted, List<string> missing)
    {
        if (!File.Exists(path))
        {
            missing.Add(path);
            return;
        }

        File.Delete(path);
        deleted.Add(path);
    }

    private static void DeleteDirectory(string path, List<string> deleted, List<string> missing)
    {
        if (!Directory.Exists(path))
        {
            missing.Add(path);
            return;
        }

        Directory.Delete(path, recursive: true);
        deleted.Add(path);
    }
}

internal sealed record JobDeleteResult(
    string JobId,
    IReadOnlyCollection<string> DeletedPaths,
    IReadOnlyCollection<string> MissingPaths);
