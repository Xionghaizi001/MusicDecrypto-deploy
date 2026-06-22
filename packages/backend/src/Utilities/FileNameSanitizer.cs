namespace MusicDecrypto.Backend;

internal static class FileNameSanitizer
{
    private const int MaxFileNameLength = 180;
    private static readonly HashSet<char> InvalidChars = Path.GetInvalidFileNameChars().ToHashSet();

    public static string Sanitize(string fileName)
    {
        var name = Path.GetFileName(CleanInvisibleCharacters(fileName));
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

    public static string CleanInvisibleCharacters(string value)
    {
        return new string(value.Select(ch => IsInvisibleOrControl(ch) ? '_' : ch).ToArray());
    }

    private static bool IsInvisibleOrControl(char ch)
    {
        return char.GetUnicodeCategory(ch) switch
        {
            System.Globalization.UnicodeCategory.Control => true,
            System.Globalization.UnicodeCategory.Format => true,
            System.Globalization.UnicodeCategory.LineSeparator => true,
            System.Globalization.UnicodeCategory.ParagraphSeparator => true,
            System.Globalization.UnicodeCategory.PrivateUse => true,
            System.Globalization.UnicodeCategory.Surrogate => true,
            System.Globalization.UnicodeCategory.OtherNotAssigned => true,
            _ => false
        };
    }
}
