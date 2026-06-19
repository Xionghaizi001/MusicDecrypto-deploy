namespace MusicDecrypto.Backend;

internal static class FileNameSanitizer
{
    private const int MaxFileNameLength = 180;
    private static readonly HashSet<char> InvalidChars = Path.GetInvalidFileNameChars().ToHashSet();

    public static string Sanitize(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return "upload.bin";
        }

        var sanitized = new string(name.Select(ch => InvalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "upload.bin";
        }

        if (sanitized.Length <= MaxFileNameLength)
        {
            return sanitized;
        }

        var extension = Path.GetExtension(sanitized);
        if (extension.Length >= MaxFileNameLength)
        {
            return sanitized[..MaxFileNameLength];
        }

        return $"{Path.GetFileNameWithoutExtension(sanitized)[..(MaxFileNameLength - extension.Length)]}{extension}";
    }
}
