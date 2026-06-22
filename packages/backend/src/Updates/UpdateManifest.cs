namespace MusicDecrypto.Backend;

internal sealed record UpdateManifest(
    string Format,
    DateTimeOffset CreatedAt,
    UpdateManifestSource? Source,
    IReadOnlyCollection<UpdateManifestFile> Files);

internal sealed record UpdateManifestFile(
    string Path,
    string Source,
    long Size,
    string Sha256,
    string? Target = null);

internal sealed record UpdateManifestSource(
    string Type,
    string? Range,
    IReadOnlyCollection<UpdateManifestCommit> Commits,
    IReadOnlyCollection<UpdateManifestRepositorySource>? Repositories = null);

internal sealed record UpdateManifestRepositorySource(
    string Target,
    string Path,
    string? Range,
    IReadOnlyCollection<UpdateManifestCommit> Commits);

internal sealed record UpdateManifestCommit(
    string Hash,
    string ShortHash,
    string Author,
    DateTimeOffset AuthoredAt,
    string Subject,
    string? Body,
    string? Target = null);
