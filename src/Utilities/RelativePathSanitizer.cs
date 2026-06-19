namespace MusicDecrypto.Backend;

internal static class RelativePathSanitizer
{
    public static string Sanitize(string relativePath)
    {
        var parts = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part is not "." and not "..")
            .Select(FileNameSanitizer.Sanitize)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        return parts.Length == 0
            ? "upload.bin"
            : Path.Combine(parts);
    }
}
