using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WhisperXApi.Configuration;
using WhisperXApi.Models;

namespace WhisperXApi.Services;

public class TranscriptionWorker : BackgroundService
{
    private readonly JobManager _jobManager;
    private readonly WhisperXOptions _options;
    private readonly ILogger<TranscriptionWorker> _logger;

    public TranscriptionWorker(
        JobManager jobManager,
        IOptions<WhisperXOptions> options,
        ILogger<TranscriptionWorker> logger)
    {
        _jobManager = jobManager;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TranscriptionWorker started");

        // Ensure temp directory exists
        Directory.CreateDirectory(_options.TempDirectory);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_jobManager.TryDequeueJob(out var job) && job != null)
            {
                await ProcessJobAsync(job, stoppingToken);
            }
            else
            {
                await Task.Delay(500, stoppingToken);
            }
        }

        _logger.LogInformation("TranscriptionWorker stopping");
    }

    private async Task ProcessJobAsync(Job job, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Processing job {JobId}", job.Id);
        job.Status = JobStatus.Processing;

        try
        {
            var result = await RunWhisperXAsync(job, stoppingToken);
            job.Result = result;
            job.Status = JobStatus.Completed;
            _logger.LogInformation("Job {JobId} completed successfully", job.Id);
        }
        catch (OperationCanceledException)
        {
            job.Error = "Job was cancelled";
            job.Status = JobStatus.Failed;
            _logger.LogWarning("Job {JobId} was cancelled", job.Id);
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            job.Status = JobStatus.Failed;
            _logger.LogError(ex, "Job {JobId} failed", job.Id);
        }
        finally
        {
            _jobManager.CleanupTempFile(job);
            _jobManager.SetCurrentProcess(null);
        }
    }

    private async Task<JsonDocument> RunWhisperXAsync(Job job, CancellationToken stoppingToken)
    {
        var profile = _jobManager.GetProfile(job.Profile);
        var outputDir = Path.Combine(_options.TempDirectory, job.Id.ToString());
        Directory.CreateDirectory(outputDir);

        try
        {
            var arguments = BuildUvxArguments(job.TempFilePath!, profile, outputDir);
            _logger.LogDebug("Running: {Executable} {Arguments}", _options.UvxPath, arguments);

            // Get the directory containing uvx.exe (and ffmpeg.exe)
            var installDir = Path.GetDirectoryName(_options.UvxPath) ?? "";

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _options.UvxPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            // Add install directory to PATH so ffmpeg can be found
            if (!string.IsNullOrEmpty(installDir))
            {
                var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                process.StartInfo.EnvironmentVariables["PATH"] = $"{installDir};{currentPath}";
            }

            // Set cache directories for uvx and Hugging Face models
            if (!string.IsNullOrEmpty(_options.CacheDirectory))
            {
                process.StartInfo.EnvironmentVariables["UV_CACHE_DIR"] = _options.CacheDirectory;
                process.StartInfo.EnvironmentVariables["HF_HOME"] = Path.Combine(_options.CacheDirectory, "huggingface");
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    stdout.AppendLine(e.Data);
                    _logger.LogDebug("[whisperx] {Output}", e.Data);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    stderr.AppendLine(e.Data);
                    _logger.LogDebug("[whisperx:err] {Output}", e.Data);
                }
            };

            process.Start();
            _jobManager.SetCurrentProcess(process);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait for process to exit or cancellation
            while (!process.HasExited)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Cancellation requested, killing whisperx process");
                    process.Kill(entireProcessTree: true);
                    throw new OperationCanceledException();
                }
                await Task.Delay(100, CancellationToken.None);
            }

            await process.WaitForExitAsync(CancellationToken.None);

            if (process.ExitCode != 0)
            {
                throw new Exception($"whisperx exited with code {process.ExitCode}: {stderr}");
            }

            // Find and parse the JSON output file
            var jsonFiles = Directory.GetFiles(outputDir, "*.json");
            if (jsonFiles.Length == 0)
            {
                throw new Exception("whisperx did not produce JSON output");
            }

            var jsonContent = await File.ReadAllTextAsync(jsonFiles[0], stoppingToken);
            return JsonDocument.Parse(jsonContent);
        }
        finally
        {
            // Clean up output directory
            if (Directory.Exists(outputDir))
            {
                try
                {
                    Directory.Delete(outputDir, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error cleaning up output directory {Dir}", outputDir);
                }
            }
        }
    }

    private string BuildUvxArguments(string inputFile, TranscriptionProfile profile, string outputDir)
    {
        var args = new List<string>();

        // Use torch-backend for automatic CUDA detection (or specify cu128, cu124, etc.)
        args.Add($"--torch-backend {_options.TorchBackend}");

        // The command to run: whisperx
        args.Add("whisperx");

        // WhisperX arguments
        args.Add($"\"{inputFile}\"");
        args.Add($"--model {profile.Model}");
        args.Add($"--device {profile.Device}");
        args.Add($"--compute_type {profile.ComputeType}");
        args.Add($"--language {profile.Language}");
        args.Add($"--align_model {profile.AlignModel}");
        args.Add($"--vad_method {profile.VadMethod}");
        args.Add("--output_format json");
        args.Add($"--output_dir \"{outputDir}\"");

        return string.Join(" ", args);
    }
}
