namespace MusicDecrypto.Backend;

internal sealed record JobRecord(
    string Id,
    string TusFileId,
    string OriginalFileName,
    string InputPath,
    string? OutputPath,
    JobStatus Status,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt)
{
    public static JobRecord Created(string id, string tusFileId, string originalFileName, string inputPath)
    {
        var now = DateTimeOffset.UtcNow;
        return new JobRecord(
            id,
            tusFileId,
            originalFileName,
            inputPath,
            OutputPath: null,
            JobStatus.Queued,
            Error: null,
            CreatedAt: now,
            UpdatedAt: now,
            StartedAt: null,
            CompletedAt: null);
    }
}

internal sealed record JobResponse(
    string Id,
    string OriginalFileName,
    JobStatus Status,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt)
{
    public static JobResponse From(JobRecord job)
    {
        return new JobResponse(
            job.Id,
            FileNameSanitizer.Sanitize(job.OriginalFileName),
            job.Status,
            SanitizePublicError(job.Error),
            job.CreatedAt,
            job.UpdatedAt,
            job.StartedAt,
            job.CompletedAt);
    }

    private static string? SanitizePublicError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        var sanitized = FileNameSanitizer.CleanInvisibleCharacters(error).Trim();
        return sanitized.Contains('/') || sanitized.Contains('\\')
            ? "Processing failed. See backend runtime logs."
            : sanitized;
    }
}
