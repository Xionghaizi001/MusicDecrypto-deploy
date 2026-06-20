namespace MusicDecrypto.Backend;

internal sealed record UpdateBatchInfo(
    string BatchId,
    string Directory,
    bool HasManifest,
    int FileCount,
    long TotalBytes,
    DateTimeOffset CreatedAt,
    UpdateManifestSource? Source);

internal sealed record UpdateApplyResult(
    string BatchId,
    string ApplyRoot,
    IReadOnlyCollection<UpdateAppliedFile> Files,
    UpdateDeploymentResult? Deployment = null);

internal sealed record UpdateAppliedFile(string Path, long Size, string Sha256);

internal sealed record UpdateDeleteResult(string BatchId, bool Deleted);

internal sealed record UpdateDeploymentResult(bool Scheduled, string Status, string? LogPath);
