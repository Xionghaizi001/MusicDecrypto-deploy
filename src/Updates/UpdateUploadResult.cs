namespace MusicDecrypto.Backend;

internal sealed record UpdateUploadResult(string BatchId, string Directory, IReadOnlyCollection<UpdateUploadedFile> Files);

internal sealed record UpdateUploadedFile(string Path, long Size);
