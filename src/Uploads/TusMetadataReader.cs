using System.Text;
using tusdotnet.Models;

namespace MusicDecrypto.Backend;

internal static class TusMetadataReader
{
    public static string? GetString(Dictionary<string, Metadata> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value)
            ? value.GetString(Encoding.UTF8)
            : null;
    }
}
