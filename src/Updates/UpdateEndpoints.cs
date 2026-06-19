using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace MusicDecrypto.Backend;

internal static class UpdateEndpoints
{
    public static void MapUpdateEndpoints(this WebApplication app)
    {
        app.MapGet("/update", () => Results.Content(UpdatePage.Html, "text/html; charset=utf-8"));

        app.MapPost("/update", UploadAsync)
            .DisableAntiforgery();
    }

    private static async Task<Results<Ok<UpdateUploadResult>, UnauthorizedHttpResult, BadRequest<string>>> UploadAsync(
        HttpRequest request,
        IOptions<AppOptions> options,
        IWebHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (!UpdateAuth.IsAuthorized(request, options.Value.ApiKey))
        {
            return TypedResults.Unauthorized();
        }

        if (!request.HasFormContentType)
        {
            return TypedResults.BadRequest("Expected multipart/form-data.");
        }

        var form = await request.ReadFormAsync(cancellationToken);
        if (form.Files.Count == 0)
        {
            return TypedResults.BadRequest("No files were uploaded.");
        }

        var paths = AppPaths.From(options.Value, environment.ContentRootPath);
        var batchId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var batchDirectory = Path.Combine(paths.Updates, batchId);
        Directory.CreateDirectory(batchDirectory);

        var savedFiles = new List<UpdateUploadedFile>();
        foreach (var file in form.Files)
        {
            if (file.Length == 0)
            {
                continue;
            }

            var relativePath = RelativePathSanitizer.Sanitize(file.FileName);
            var targetPath = Path.GetFullPath(Path.Combine(batchDirectory, relativePath));
            if (!targetPath.StartsWith(batchDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !string.Equals(targetPath, batchDirectory, StringComparison.Ordinal))
            {
                return TypedResults.BadRequest($"Invalid file path: {file.FileName}");
            }

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

        if (savedFiles.Count == 0)
        {
            return TypedResults.BadRequest("Uploaded files were empty.");
        }

        return TypedResults.Ok(new UpdateUploadResult(batchId, batchDirectory, savedFiles));
    }
}
