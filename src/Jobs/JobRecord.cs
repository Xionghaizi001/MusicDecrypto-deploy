namespace MusicDecrypto.Backend;

internal sealed record JobRecord(
    string Id,
    string TusFileId,
    string OriginalFileName,
    string InputPath,
    string? OutputPath,
    JobStatus Status,
    string? Error,
    string? Log,
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
            Log: null,
            CreatedAt: now,
            UpdatedAt: now,
            StartedAt: null,
            CompletedAt: null);
    }
}
