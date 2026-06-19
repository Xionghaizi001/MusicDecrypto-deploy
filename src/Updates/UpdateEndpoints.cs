using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace MusicDecrypto.Backend;

internal static class UpdateEndpoints
{
    public static void MapUpdateEndpoints(this WebApplication app)
    {
        app.MapGet("/update", () => Results.Content(UpdatePage.Html, "text/html; charset=utf-8"));

        app.MapGet("/update/batches", ListAsync);

        app.MapPost("/update", UploadAsync)
            .DisableAntiforgery();

        app.MapPost("/update/{batchId}/apply", ApplyAsync);

        app.MapDelete("/update/{batchId}", DeleteAsync);
    }

    private static Results<Ok<IReadOnlyCollection<UpdateBatchInfo>>, UnauthorizedHttpResult> ListAsync(
        HttpRequest request,
        IOptions<AppOptions> options,
        IWebHostEnvironment environment)
    {
        if (!UpdateAuth.IsAuthorized(request, options.Value.ApiKey))
        {
            return TypedResults.Unauthorized();
        }

        var paths = AppPaths.From(options.Value, environment.ContentRootPath);
        return TypedResults.Ok(UpdatePackageService.ListBatches(paths.Updates));
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
        try
        {
            var result = await UpdatePackageService.SaveUploadAsync(form.Files, paths.Updates, cancellationToken);
            return TypedResults.Ok(result);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Results<Ok<UpdateApplyResult>, UnauthorizedHttpResult, NotFound, BadRequest<string>>> ApplyAsync(
        string batchId,
        HttpRequest request,
        IOptions<AppOptions> options,
        IWebHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (!UpdateAuth.IsAuthorized(request, options.Value.ApiKey))
        {
            return TypedResults.Unauthorized();
        }

        var paths = AppPaths.From(options.Value, environment.ContentRootPath);
        try
        {
            var result = await UpdatePackageService.ApplyAsync(paths.Updates, paths.UpdateApplyRoot, batchId, cancellationToken);
            return TypedResults.Ok(result);
        }
        catch (DirectoryNotFoundException)
        {
            return TypedResults.NotFound();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static Results<Ok<UpdateDeleteResult>, UnauthorizedHttpResult> DeleteAsync(
        string batchId,
        HttpRequest request,
        IOptions<AppOptions> options,
        IWebHostEnvironment environment)
    {
        if (!UpdateAuth.IsAuthorized(request, options.Value.ApiKey))
        {
            return TypedResults.Unauthorized();
        }

        var paths = AppPaths.From(options.Value, environment.ContentRootPath);
        return TypedResults.Ok(UpdatePackageService.Delete(paths.Updates, batchId));
    }
}
