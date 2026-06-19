using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using MusicDecrypto.Backend;
using tusdotnet;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;
using tusdotnet.Stores;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = null;
});

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("MusicDecrypto"));
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddHostedService<DecryptionWorker>();

var app = builder.Build();

app.UseMiddleware<ApiKeyMiddleware>();

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    service = "musicdecrypto-backend",
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/api/jobs", (JobStore jobs) => Results.Ok(jobs.GetAll()));

app.MapGet("/api/jobs/{id}", Results<Ok<JobRecord>, NotFound> (string id, JobStore jobs) =>
{
    var job = jobs.Get(id);
    return job is null ? TypedResults.NotFound() : TypedResults.Ok(job);
});

app.MapGet("/api/jobs/{id}/download", Results<PhysicalFileHttpResult, NotFound, BadRequest<string>> (string id, JobStore jobs) =>
{
    var job = jobs.Get(id);
    if (job is null)
    {
        return TypedResults.NotFound();
    }

    if (job.Status != JobStatus.Completed || string.IsNullOrWhiteSpace(job.OutputPath))
    {
        return TypedResults.BadRequest("Job is not completed.");
    }

    if (!File.Exists(job.OutputPath))
    {
        return TypedResults.NotFound();
    }

    return TypedResults.PhysicalFile(
        job.OutputPath,
        contentType: "application/octet-stream",
        fileDownloadName: Path.GetFileName(job.OutputPath));
});

app.UseTus(httpContext =>
{
    var options = httpContext.RequestServices.GetRequiredService<IOptions<AppOptions>>().Value;
    var paths = AppPaths.From(options, app.Environment.ContentRootPath);
    Directory.CreateDirectory(paths.TusStore);

    return new DefaultTusConfiguration
    {
        UrlPath = "/files",
        Store = new TusDiskStore(paths.TusStore),
        Events = new Events
        {
            OnFileCompleteAsync = async eventContext =>
            {
                var file = await eventContext.GetFileAsync();
                var metadata = await file.GetMetadataAsync(eventContext.CancellationToken);
                var originalName = TusMetadataReader.GetString(metadata, "filename") ?? $"{file.Id}.bin";
                var safeName = FileNameSanitizer.Sanitize(originalName);

                var jobId = Guid.NewGuid().ToString("N");
                var inputPath = Path.Combine(paths.Uploads, $"{jobId}-{safeName}");
                Directory.CreateDirectory(paths.Uploads);

                await using (var source = await file.GetContentAsync(eventContext.CancellationToken))
                await using (var target = File.Create(inputPath))
                {
                    await source.CopyToAsync(target, eventContext.CancellationToken);
                }

                var job = JobRecord.Created(jobId, file.Id, originalName, inputPath);
                var jobs = eventContext.HttpContext.RequestServices.GetRequiredService<JobStore>();
                var queue = eventContext.HttpContext.RequestServices.GetRequiredService<JobQueue>();
                await jobs.UpsertAsync(job, eventContext.CancellationToken);
                await queue.EnqueueAsync(jobId, eventContext.CancellationToken);
            }
        }
    };
});

app.Run();
