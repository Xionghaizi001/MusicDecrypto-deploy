using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;
using Microsoft.Extensions.Options;
using MusicDecrypto.Backend;
using tusdotnet;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;
using tusdotnet.Stores;

var builder = WebApplication.CreateBuilder(args);

const int LargeFileBufferSize = 1024 * 1024;

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = null;
});

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("MusicDecrypto"));
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
builder.Services.AddCors(options =>
{
    var allowedOrigins = builder.Configuration
        .GetSection("MusicDecrypto:AllowedOrigins")
        .GetChildren()
        .Select(origin => origin.Value)
        .Where(origin => !string.IsNullOrWhiteSpace(origin))
        .Cast<string>()
        .ToArray();

    options.AddPolicy("Frontend", policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins);
        }

        policy
            .WithMethods("GET", "POST", "HEAD", "PATCH", "DELETE", "OPTIONS")
            .WithHeaders(
                "Authorization",
                "X-Api-Key",
                "X-Update-Key",
                "Content-Type",
                "Tus-Resumable",
                "Upload-Length",
                "Upload-Metadata",
                "Upload-Offset",
                "Upload-Defer-Length")
            .WithExposedHeaders(
                "Location",
                "Content-Disposition",
                "Tus-Resumable",
                "Upload-Offset",
                "Upload-Length",
                "Upload-Metadata",
                "Upload-Expires");
    });
});
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<JobDeletionService>();
builder.Services.AddSingleton<JobRuntimeLogService>();
builder.Services.AddSingleton<UpdateDeploymentService>();
builder.Services.AddHostedService<DecryptionWorker>();
builder.Services.AddHostedService<JobCleanupWorker>();

var app = builder.Build();

app.UseCors("Frontend");
app.UseMiddleware<ApiKeyMiddleware>();

app.MapUpdateEndpoints();

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    service = "musicdecrypto-backend",
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/api/jobs", Results<Ok<IReadOnlyCollection<JobResponse>>, BadRequest<string>> (
    HttpRequest request,
    JobStore jobs) =>
{
    var validationError = ValidateApiRequestShape(request);
    if (validationError is not null)
    {
        return TypedResults.BadRequest(validationError);
    }

    return TypedResults.Ok((IReadOnlyCollection<JobResponse>)jobs.GetAll().Select(JobResponse.From).ToArray());
});

app.MapGet("/api/jobs/{id}", Results<Ok<JobResponse>, NotFound, BadRequest<string>> (
    string id,
    HttpRequest request,
    JobStore jobs) =>
{
    var validationError = ValidateJobApiRequest(request, id);
    if (validationError is not null)
    {
        return TypedResults.BadRequest(validationError);
    }

    var job = jobs.Get(id);
    return job is null ? TypedResults.NotFound() : TypedResults.Ok(JobResponse.From(job));
});

app.MapGet("/api/jobs/{id}/download", IResult (
    string id,
    JobStore jobs,
    HttpContext httpContext,
    IOptions<AppOptions> options,
    IWebHostEnvironment environment) =>
{
    var validationError = ValidateJobApiRequest(httpContext.Request, id);
    if (validationError is not null)
    {
        return Results.BadRequest(validationError);
    }

    var job = jobs.Get(id);
    if (job is null)
    {
        return Results.NotFound();
    }

    if (job.Status != JobStatus.Completed || string.IsNullOrWhiteSpace(job.OutputPath))
    {
        return Results.BadRequest("Job is not completed.");
    }

    if (!File.Exists(job.OutputPath))
    {
        return Results.NotFound();
    }

    httpContext.Response.Headers[HeaderNames.ContentDisposition] =
        BuildContentDisposition(FileNameSanitizer.Sanitize(Path.GetFileName(job.OutputPath)));

    var paths = AppPaths.From(options.Value, environment.ContentRootPath);
    var xAccelRedirect = BuildXAccelRedirect(job.OutputPath, paths.Outputs);
    if (xAccelRedirect is not null)
    {
        httpContext.Response.Headers["X-Accel-Redirect"] = xAccelRedirect;
        httpContext.Response.ContentType = "application/octet-stream";
        return TypedResults.Empty;
    }

    return TypedResults.PhysicalFile(
        job.OutputPath,
        contentType: "application/octet-stream");
});

app.MapDelete(
    "/api/jobs/{id}",
    DeleteJobAsync);

app.MapPost(
    "/api/jobs/{id}/delete",
    DeleteJobAsync);

app.UseTus(httpContext =>
{
    var options = httpContext.RequestServices.GetRequiredService<IOptions<AppOptions>>().Value;
    var paths = AppPaths.From(options, app.Environment.ContentRootPath);
    Directory.CreateDirectory(paths.TusStore);

    return new DefaultTusConfiguration
    {
        UrlPath = "/files",
        Store = new TusDiskStore(
            paths.TusStore,
            deletePartialFilesOnConcat: true,
            bufferSize: new TusDiskBufferSize(LargeFileBufferSize, LargeFileBufferSize)),
        Events = new Events
        {
            OnBeforeCreateAsync = eventContext =>
            {
                var unsupportedMetadata = eventContext.Metadata.Keys
                    .Where(key => !string.Equals(key, "filename", StringComparison.Ordinal))
                    .ToArray();

                if (unsupportedMetadata.Length > 0)
                {
                    eventContext.FailRequest("Unsupported upload metadata.");
                }

                return Task.CompletedTask;
            },
            OnFileCompleteAsync = async eventContext =>
            {
                var logger = eventContext.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("MusicDecrypto.Backend.Uploads");

                try
                {
                    var file = await eventContext.GetFileAsync();
                    var metadata = await file.GetMetadataAsync(eventContext.CancellationToken);
                    var originalName = FileNameSanitizer.Sanitize(
                        TusMetadataReader.GetString(metadata, "filename") ?? $"{file.Id}.bin");
                    var safeName = FileNameSanitizer.Sanitize(originalName);

                    var jobId = Guid.NewGuid().ToString("N");
                    var inputPath = Path.Combine(paths.Uploads, $"{jobId}-{safeName}");
                    Directory.CreateDirectory(paths.Uploads);

                    logger.LogInformation(
                        "Tus upload completed. TusFileId={TusFileId}, OriginalFileName={OriginalFileName}, InputPath={InputPath}",
                        file.Id,
                        originalName,
                        inputPath);

                    await using (var source = await file.GetContentAsync(eventContext.CancellationToken))
                    await using (var target = new FileStream(
                                     inputPath,
                                     FileMode.Create,
                                     FileAccess.Write,
                                     FileShare.None,
                                     LargeFileBufferSize,
                                     FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        await source.CopyToAsync(target, LargeFileBufferSize, eventContext.CancellationToken);
                    }

                    var inputBytes = new FileInfo(inputPath).Length;
                    var job = JobRecord.Created(jobId, file.Id, originalName, inputPath);
                    var jobs = eventContext.HttpContext.RequestServices.GetRequiredService<JobStore>();
                    var queue = eventContext.HttpContext.RequestServices.GetRequiredService<JobQueue>();
                    await jobs.UpsertAsync(job, eventContext.CancellationToken);
                    await queue.EnqueueAsync(jobId, eventContext.CancellationToken);

                    logger.LogInformation(
                        "Created decryption job from upload. JobId={JobId}, TusFileId={TusFileId}, InputPath={InputPath}, InputBytes={InputBytes}",
                        jobId,
                        file.Id,
                        inputPath,
                        inputBytes);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Failed while finalizing tus upload and creating decryption job.");
                    throw;
                }
            }
        }
    };
});

app.Run();

static async Task<Results<Ok<JobDeleteResult>, NotFound, Conflict<string>, BadRequest<string>>> DeleteJobAsync(
    string id,
    HttpRequest request,
    JobStore jobs,
    JobDeletionService deletion,
    CancellationToken cancellationToken)
{
    var validationError = ValidateJobApiRequest(request, id);
    if (validationError is not null)
    {
        return TypedResults.BadRequest(validationError);
    }

    var job = jobs.Get(id);
    if (job is null)
    {
        return TypedResults.NotFound();
    }

    if (job.Status == JobStatus.Running)
    {
        return TypedResults.Conflict("Running jobs cannot be deleted.");
    }

    try
    {
        var result = deletion.DeleteFiles(job);
        var removed = await jobs.RemoveAsync(id, cancellationToken);
        return removed ? TypedResults.Ok(result) : TypedResults.NotFound();
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return TypedResults.BadRequest(ex.Message);
    }
}

static string BuildContentDisposition(string fileName)
{
    fileName = FileNameSanitizer.Sanitize(fileName);
    var fallback = BuildAsciiFileNameFallback(fileName);
    var encoded = Uri.EscapeDataString(fileName);

    return $"attachment; filename=\"{fallback}\"; filename*=UTF-8''{encoded}";
}

static string? ValidateJobApiRequest(HttpRequest request, string jobId)
{
    if (!IsValidJobId(jobId))
    {
        return "Invalid job id.";
    }

    return ValidateApiRequestShape(request);
}

static string? ValidateApiRequestShape(HttpRequest request)
{
    if (request.Query.Count > 0)
    {
        return "Unexpected query fields.";
    }

    if (HasRequestBodyData(request))
    {
        return "Unexpected request body.";
    }

    return null;
}

static bool IsValidJobId(string id)
{
    return id.Length == 32 && id.All(Uri.IsHexDigit);
}

static bool HasRequestBodyData(HttpRequest request)
{
    if (request.ContentLength is > 0)
    {
        return true;
    }

    return request.Headers.TransferEncoding.Count > 0;
}

static string BuildAsciiFileNameFallback(string fileName)
{
    var fallback = new string(fileName
        .Select(ch => ch is >= ' ' and <= '~' && ch is not '"' and not '\\' ? ch : '_')
        .ToArray())
        .Trim();

    return string.IsNullOrWhiteSpace(fallback) ? "download.bin" : fallback;
}

static string? BuildXAccelRedirect(string outputPath, string outputsRoot)
{
    var fullOutputPath = Path.GetFullPath(outputPath);
    var fullOutputsRoot = Path.GetFullPath(outputsRoot);
    var relativePath = Path.GetRelativePath(fullOutputsRoot, fullOutputPath);

    if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
    {
        return null;
    }

    var segments = relativePath
        .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
        .Select(Uri.EscapeDataString);

    return $"/_musicdecrypto_outputs/{string.Join('/', segments)}";
}
