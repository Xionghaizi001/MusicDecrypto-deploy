namespace MusicDecrypto.Backend;

internal sealed record UpdateManifest(
    string Format,
    DateTimeOffset CreatedAt,
    IReadOnlyCollection<UpdateManifestFile> Files);

internal sealed record UpdateManifestFile(
    string Path,
    string Source,
    long Size,
    string Sha256);
