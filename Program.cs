using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using WhisperXApi.Configuration;
using WhisperXApi.Models;
using WhisperXApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Enable Windows Service support
builder.Host.UseWindowsService();

// Configure Kestrel for large file uploads (500MB max)
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 500 * 1024 * 1024; // 500 MB
});

// Configure form options for large file uploads
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 500 * 1024 * 1024; // 500 MB
});

// Configure JSON serialization
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// Configure WhisperX options from appsettings.json
builder.Services.Configure<WhisperXOptions>(
    builder.Configuration.GetSection(WhisperXOptions.SectionName));

// Register services
builder.Services.AddSingleton<JobManager>();
builder.Services.AddHostedService<TranscriptionWorker>();
builder.Services.AddHostedService<TimeoutCleanupService>();

var app = builder.Build();

// Ensure temp directory exists on startup
var options = app.Services.GetRequiredService<IOptions<WhisperXOptions>>().Value;
Directory.CreateDirectory(options.TempDirectory);

// Initialize or load API key
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var apiKeyFilePath = Path.Combine(options.TempDirectory, "api-key.txt");
string apiKey;

if (!string.IsNullOrEmpty(options.ApiKey))
{
    // Use configured key from appsettings.json
    apiKey = options.ApiKey;
    logger.LogInformation("Using API key from configuration");
}
else if (File.Exists(apiKeyFilePath))
{
    // Load existing key from file
    apiKey = File.ReadAllText(apiKeyFilePath).Trim();
    logger.LogInformation("Loaded API key from {Path}", apiKeyFilePath);
}
else
{
    // Generate new key and save to file (hex string for readability)
    apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    File.WriteAllText(apiKeyFilePath, apiKey);
    logger.LogInformation("Generated new API key and saved to {Path}", apiKeyFilePath);
}

// Helper to validate API key
IResult? ValidateApiKey(HttpRequest request)
{
    var providedApiKey = request.Headers["X-API-Key"].FirstOrDefault();
    if (string.IsNullOrEmpty(providedApiKey) || !string.Equals(providedApiKey, apiKey, StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }
    return null;
}

// POST /jobs - Create a new transcription job
app.MapPost("/jobs", async (HttpRequest request, JobManager jobManager, IOptions<WhisperXOptions> opts) =>
{
    var authError = ValidateApiKey(request);
    if (authError != null) return authError;

    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Content-Type must be multipart/form-data" });
    }

    var form = await request.ReadFormAsync();

    // Get profile (default to "default")
    var profile = form["profile"].FirstOrDefault() ?? "default";

    // Validate profile exists
    if (!opts.Value.Profiles.ContainsKey(profile))
    {
        return Results.BadRequest(new { error = $"Unknown profile: {profile}" });
    }

    // Get optional temperature parameter
    double? temperature = null;
    var temperatureStr = form["temperature"].FirstOrDefault();
    if (!string.IsNullOrEmpty(temperatureStr) && double.TryParse(temperatureStr, out var tempValue))
    {
        temperature = tempValue;
    }

    // Get optional initial_prompt parameter
    var initialPrompt = form["initial_prompt"].FirstOrDefault();

    // Get optional VAD chunking parameters (for Parakeet)
    double? vadMergeGap = null;
    var vadMergeGapStr = form["vad_merge_gap"].FirstOrDefault();
    if (!string.IsNullOrEmpty(vadMergeGapStr) && double.TryParse(vadMergeGapStr, out var mergeGapValue))
    {
        vadMergeGap = mergeGapValue;
    }

    double? vadMaxChunk = null;
    var vadMaxChunkStr = form["vad_max_chunk"].FirstOrDefault();
    if (!string.IsNullOrEmpty(vadMaxChunkStr) && double.TryParse(vadMaxChunkStr, out var maxChunkValue))
    {
        vadMaxChunk = maxChunkValue;
    }

    double? vadSplitGap = null;
    var vadSplitGapStr = form["vad_split_gap"].FirstOrDefault();
    if (!string.IsNullOrEmpty(vadSplitGapStr) && double.TryParse(vadSplitGapStr, out var splitGapValue))
    {
        vadSplitGap = splitGapValue;
    }

    // Get file
    var file = form.Files.GetFile("file");
    if (file == null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "No file provided" });
    }

    // Validate file extension
    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (extension != ".wav" && extension != ".flac")
    {
        return Results.BadRequest(new { error = "File must be .wav or .flac" });
    }

    // Save file to temp directory
    var jobId = Guid.NewGuid();
    var tempFilePath = Path.Combine(opts.Value.TempDirectory, $"{jobId}{extension}");

    await using (var stream = File.Create(tempFilePath))
    {
        await file.CopyToAsync(stream);
    }

    // Create job (this will generate a new ID, so we need to update our temp file)
    var job = jobManager.CreateJob(profile, tempFilePath, temperature, initialPrompt, vadMergeGap, vadMaxChunk, vadSplitGap);

    // Rename file to match actual job ID
    var newTempFilePath = Path.Combine(opts.Value.TempDirectory, $"{job.Id}{extension}");
    if (tempFilePath != newTempFilePath)
    {
        File.Move(tempFilePath, newTempFilePath);
        job.TempFilePath = newTempFilePath;
    }

    return Results.Created($"/jobs/{job.Id}", new { id = job.Id });
});

// GET /jobs - List all jobs
app.MapGet("/jobs", (HttpRequest request, JobManager jobManager) =>
{
    var authError = ValidateApiKey(request);
    if (authError != null) return authError;

    var jobs = jobManager.GetAllJobs().Select(job => new
    {
        id = job.Id,
        status = job.Status.ToString().ToLowerInvariant(),
        profile = job.Profile,
        createdAt = job.CreatedAt,
        error = job.Status == JobStatus.Failed ? job.Error : null
    });
    return Results.Ok(new { jobs });
});

// GET /jobs/{id} - Get job status and result
app.MapGet("/jobs/{id:guid}", (HttpRequest request, Guid id, JobManager jobManager) =>
{
    var authError = ValidateApiKey(request);
    if (authError != null) return authError;

    var job = jobManager.GetJob(id);
    if (job == null)
    {
        return Results.NotFound(new { error = "Job not found" });
    }

    return job.Status switch
    {
        JobStatus.Queued => Results.Ok(new
        {
            id = job.Id,
            status = "queued"
        }),
        JobStatus.Processing => Results.Ok(new
        {
            id = job.Id,
            status = "processing",
            progress = job.ProgressStage != null ? new
            {
                stage = job.ProgressStage,
                current = job.ProgressCurrent,
                total = job.ProgressTotal
            } : null
        }),
        JobStatus.Completed => Results.Ok(new
        {
            id = job.Id,
            status = "completed",
            result = job.Result
        }),
        JobStatus.Failed => Results.Ok(new
        {
            id = job.Id,
            status = "failed",
            error = job.Error
        }),
        _ => Results.Ok(new { id = job.Id, status = job.Status.ToString().ToLowerInvariant() })
    };
});

// DELETE /jobs/{id} - Delete a job
app.MapDelete("/jobs/{id:guid}", (HttpRequest request, Guid id, JobManager jobManager) =>
{
    var authError = ValidateApiKey(request);
    if (authError != null) return authError;

    var deleted = jobManager.DeleteJob(id);
    return deleted ? Results.NoContent() : Results.NotFound(new { error = "Job not found" });
});

// POST /shutdown - Initiate machine shutdown
app.MapPost("/shutdown", (HttpRequest request, IHostApplicationLifetime appLifetime, ILogger<Program> log) =>
{
    var authError = ValidateApiKey(request);
    if (authError != null) return authError;

    log.LogInformation("Machine shutdown requested via API");

    // Schedule machine shutdown with 5 second delay to allow response to be sent
    var shutdownProcess = new System.Diagnostics.Process
    {
        StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "shutdown",
            Arguments = "/s /t 5 /c \"Shutdown requested via WhisperX API\"",
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };
    shutdownProcess.Start();

    // Also stop the application gracefully
    appLifetime.StopApplication();

    return Results.Ok(new { message = "Machine shutdown initiated" });
});

app.Run();
