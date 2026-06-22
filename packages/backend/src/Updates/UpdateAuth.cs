using System.Security.Cryptography;
using System.Text;

namespace MusicDecrypto.Backend;

internal static class UpdateAuth
{
    public static bool IsAuthorized(HttpRequest request, string? configuredApiKey)
    {
        if (string.IsNullOrWhiteSpace(configuredApiKey))
        {
            return false;
        }

        var suppliedKey = GetSuppliedKey(request);
        if (string.IsNullOrEmpty(suppliedKey))
        {
            return false;
        }

        var expectedKey = Reverse(configuredApiKey);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedKey);
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedKey);

        return expectedBytes.Length == suppliedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static string? GetSuppliedKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Update-Key", out var headerKey))
        {
            return headerKey.ToString();
        }

        return request.Query.TryGetValue("key", out var queryKey)
            ? queryKey.ToString()
            : null;
    }

    private static string Reverse(string value)
    {
        var chars = value.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }
}
