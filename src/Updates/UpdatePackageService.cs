using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace MusicDecrypto.Backend;

internal static class UpdatePackageService
{
    public const string ManifestFileName = "musicdecrypto-update.json";
    private const string ManifestFormat = "musicdecrypto.update.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static IReadOnlyCollection<UpdateBatchInfo> ListBatches(string updateRoot)
    {
        if (!Directory.Exists(updateRoot))
        {
            return [];
        }

        return Directory.EnumerateDirectories(updateRoot)
            .Select(directory =>
            {
                var manifest = TryReadManifest(directory);
                var files = manifest?.Files ?? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Where(path => !string.Equals(Path.GetFileName(path), ManifestFileName, StringComparison.Ordinal))
                    .Select(path =>
                    {
                        var info = new FileInfo(path);
                        var relativePath = Path.GetRelativePath(directory, path).Replace('\\', '/');
                        return new UpdateManifestFile(relativePath, relativePath, info.Length, string.Empty);
                    })
                    .ToArray();

                var createdAt = Directory.GetCreationTimeUtc(directory);
                return new UpdateBatchInfo(
                    Path.GetFileName(directory),
                    directory,
                    manifest is not null,
                    files.Count,
                    files.Sum(file => file.Size),
                    new DateTimeOffset(createdAt, TimeSpan.Zero));
            })
            .OrderByDescending(batch => batch.CreatedAt)
            .ToArray();
    }

    public static async Task<UpdateUploadResult> SaveUploadAsync(
        IFormFileCollection files,
        string updateRoot,
        CancellationToken cancellationToken)
    {
        var batchId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var batchDirectory = Path.Combine(updateRoot, batchId);
        Directory.CreateDirectory(batchDirectory);

        var savedFiles = new List<UpdateUploadedFile>();
        if (files.Count == 1 && IsZip(files[0].FileName))
        {
            await SaveZipBatchAsync(files[0], batchDirectory, savedFiles, cancellationToken);
        }
        else
        {
            await SaveLooseFilesAsync(files, batchDirectory, savedFiles, cancellationToken);
            await WriteManifestAsync(batchDirectory, savedFiles, cancellationToken);
        }

        if (savedFiles.Count == 0)
        {
            Directory.Delete(batchDirectory, recursive: true);
            throw new InvalidOperationException("Uploaded files were empty.");
        }

        return new UpdateUploadResult(batchId, batchDirectory, savedFiles);
    }

    public static async Task<UpdateApplyResult> ApplyAsync(
        string updateRoot,
        string applyRoot,
        string batchId,
        CancellationToken cancellationToken)
    {
        var batchDirectory = ResolveBatchDirectory(updateRoot, batchId);
        if (!Directory.Exists(batchDirectory))
        {
            throw new DirectoryNotFoundException($"Update batch not found: {batchId}");
        }

        var manifest = ReadManifest(batchDirectory);
        Directory.CreateDirectory(applyRoot);
        var fullApplyRoot = Path.GetFullPath(applyRoot);
        var appliedFiles = new List<UpdateAppliedFile>();

        foreach (var file in manifest.Files)
        {
            var sourceRelativePath = RelativePathSanitizer.Sanitize(file.Source);
            var targetRelativePath = RelativePathSanitizer.Sanitize(file.Path);
            var sourcePath = Path.GetFullPath(Path.Combine(batchDirectory, sourceRelativePath));
            var targetPath = Path.GetFullPath(Path.Combine(fullApplyRoot, targetRelativePath));

            EnsureInside(batchDirectory, sourcePath, "source");
            EnsureInside(fullApplyRoot, targetPath, "target");

            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"Manifest source file not found: {file.Source}", sourcePath);
            }

            var sourceInfo = new FileInfo(sourcePath);
            if (sourceInfo.Length != file.Size)
            {
                throw new InvalidOperationException($"Size mismatch for {file.Path}.");
            }

            var actualSha256 = await ComputeSha256Async(sourcePath, cancellationToken);
            if (!string.Equals(actualSha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"SHA-256 mismatch for {file.Path}.");
            }

            var targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            File.Copy(sourcePath, targetPath, overwrite: true);
            appliedFiles.Add(new UpdateAppliedFile(targetRelativePath.Replace('\\', '/'), sourceInfo.Length, actualSha256));
        }

        return new UpdateApplyResult(batchId, fullApplyRoot, appliedFiles);
    }

    public static UpdateDeleteResult Delete(string updateRoot, string batchId)
    {
        var batchDirectory = ResolveBatchDirectory(updateRoot, batchId);
        if (!Directory.Exists(batchDirectory))
        {
            return new UpdateDeleteResult(batchId, false);
        }

        Directory.Delete(batchDirectory, recursive: true);
        return new UpdateDeleteResult(batchId, true);
    }

    private static async Task SaveLooseFilesAsync(
        IFormFileCollection files,
        string batchDirectory,
        List<UpdateUploadedFile> savedFiles,
        CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            if (file.Length == 0)
            {
                continue;
            }

            var relativePath = RelativePathSanitizer.Sanitize(file.FileName);
            var targetPath = Path.GetFullPath(Path.Combine(batchDirectory, "files", relativePath));
            EnsureInside(batchDirectory, targetPath, "target");

            var targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            await using var source = file.OpenReadStream();
            await using var target = File.Create(targetPath);
            await source.CopyToAsync(target, cancellationToken);

            savedFiles.Add(new UpdateUploadedFile(relativePath, file.Length));
        }
    }

    private static async Task SaveZipBatchAsync(
        IFormFile file,
        string batchDirectory,
        List<UpdateUploadedFile> savedFiles,
        CancellationToken cancellationToken)
    {
        var zipPath = Path.Combine(batchDirectory, "uploaded.zip");
        await using (var source = file.OpenReadStream())
        await using (var target = File.Create(zipPath))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.GetEntry(ManifestFileName) is null)
        {
            throw new InvalidOperationException($"Zip package must contain {ManifestFileName}.");
        }

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var relativePath = RelativePathSanitizer.Sanitize(entry.FullName);
            var targetPath = Path.GetFullPath(Path.Combine(batchDirectory, relativePath));
            EnsureInside(batchDirectory, targetPath, "target");

            var targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            entry.ExtractToFile(targetPath, overwrite: true);
        }

        File.Delete(zipPath);
        var manifest = ReadManifest(batchDirectory);
        foreach (var manifestFile in manifest.Files)
        {
            savedFiles.Add(new UpdateUploadedFile(manifestFile.Path, manifestFile.Size));
        }
    }

    private static async Task WriteManifestAsync(
        string batchDirectory,
        IReadOnlyCollection<UpdateUploadedFile> files,
        CancellationToken cancellationToken)
    {
        var manifestFiles = new List<UpdateManifestFile>();
        foreach (var file in files)
        {
            var source = $"files/{file.Path}";
            var sourcePath = Path.Combine(batchDirectory, source);
            var sha256 = await ComputeSha256Async(sourcePath, cancellationToken);
            manifestFiles.Add(new UpdateManifestFile(file.Path, source, file.Size, sha256));
        }

        var manifest = new UpdateManifest(ManifestFormat, DateTimeOffset.UtcNow, manifestFiles);
        var manifestPath = Path.Combine(batchDirectory, ManifestFileName);
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);
    }

    private static UpdateManifest ReadManifest(string batchDirectory)
    {
        var manifestPath = Path.Combine(batchDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Update manifest not found: {ManifestFileName}", manifestPath);
        }

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidOperationException("Update manifest is empty or invalid.");

        if (!string.Equals(manifest.Format, ManifestFormat, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported update manifest format: {manifest.Format}");
        }

        if (manifest.Files.Count == 0)
        {
            throw new InvalidOperationException("Update manifest does not list any files.");
        }

        return manifest;
    }

    private static UpdateManifest? TryReadManifest(string batchDirectory)
    {
        try
        {
            return ReadManifest(batchDirectory);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveBatchDirectory(string updateRoot, string batchId)
    {
        var safeBatchId = FileNameSanitizer.Sanitize(batchId);
        var fullUpdateRoot = Path.GetFullPath(updateRoot);
        var batchDirectory = Path.GetFullPath(Path.Combine(fullUpdateRoot, safeBatchId));
        EnsureInside(fullUpdateRoot, batchDirectory, "batch");
        return batchDirectory;
    }

    private static bool IsZip(string fileName)
    {
        return string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureInside(string root, string path, string label)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Invalid {label} path outside allowed root.");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
