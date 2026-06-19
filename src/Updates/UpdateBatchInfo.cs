namespace MusicDecrypto.Backend;

internal sealed record UpdateBatchInfo(
    string BatchId,
    string Directory,
    bool HasManifest,
    int FileCount,
    long TotalBytes,
    DateTimeOffset CreatedAt);

internal sealed record UpdateApplyResult(
    string BatchId,
    string ApplyRoot,
    IReadOnlyCollection<UpdateAppliedFile> Files);

internal sealed record UpdateAppliedFile(string Path, long Size, string Sha256);

internal sealed record UpdateDeleteResult(string BatchId, bool Deleted);
