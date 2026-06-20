using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace MusicDecrypto.Backend;

internal sealed class ApiKeyMiddleware(
    RequestDelegate next,
    IOptions<AppOptions> options,
    ILogger<ApiKeyMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var configuredKey = options.Value.ApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(configuredKey) ||
            HttpMethods.IsOptions(context.Request.Method) ||
            context.Request.Path.StartsWithSegments("/healthz"))
        {
            await next(context);
            return;
        }

        if (!context.Request.Path.StartsWithSegments("/api") &&
            !context.Request.Path.StartsWithSegments("/files"))
        {
            await next(context);
            return;
        }

        var suppliedKey = GetSuppliedKey(context.Request);
        if (ApiKeysMatch(configuredKey, suppliedKey))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        logger.LogWarning(
            "Rejected unauthorized request. Method={Method}, Path={Path}, HasXApiKey={HasXApiKey}, HasAuthorization={HasAuthorization}, RemoteIp={RemoteIp}",
            context.Request.Method,
            context.Request.Path,
            context.Request.Headers.ContainsKey("X-Api-Key"),
            context.Request.Headers.ContainsKey("Authorization"),
            context.Connection.RemoteIpAddress);
        await context.Response.WriteAsync("Unauthorized");
    }

    private static string? GetSuppliedKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Api-Key", out var apiKey))
        {
            return apiKey.ToString();
        }

        var authorization = request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        return authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[bearerPrefix.Length..]
            : null;
    }

    private static bool ApiKeysMatch(string configuredKey, string? suppliedKey)
    {
        if (string.IsNullOrEmpty(suppliedKey))
        {
            return false;
        }

        var configuredBytes = Encoding.UTF8.GetBytes(configuredKey);
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedKey);
        return configuredBytes.Length == suppliedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(configuredBytes, suppliedBytes);
    }
}
