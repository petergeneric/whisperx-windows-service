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
            var result = await RunTranscriptionAsync(job, stoppingToken);
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

    private async Task<JsonDocument> RunTranscriptionAsync(Job job, CancellationToken stoppingToken)
    {
        var profile = _jobManager.GetProfile(job.Profile);
        var outputDir = Path.Combine(_options.TempDirectory, job.Id.ToString());
        Directory.CreateDirectory(outputDir);

        // Convert .flac to 16kHz mono .wav using ffmpeg (shared for all engines)
        string? convertedWavPath = null;
        var inputFile = job.TempFilePath!;
        if (Path.GetExtension(inputFile).Equals(".flac", StringComparison.OrdinalIgnoreCase))
        {
            convertedWavPath = Path.Combine(_options.TempDirectory, $"{job.Id}.wav");
            await ConvertFlacToWavAsync(inputFile, convertedWavPath, stoppingToken);
            inputFile = convertedWavPath;
        }

        try
        {
            // Dispatch based on engine type
            return profile.Engine.ToLowerInvariant() switch
            {
                "parakeet" => await RunParakeetAsync(inputFile, job, profile, outputDir, stoppingToken),
                _ => await RunWhisperXAsync(inputFile, job, profile, outputDir, stoppingToken)
            };
        }
        finally
        {
            // Clean up converted wav file if we created one
            if (convertedWavPath != null && File.Exists(convertedWavPath))
            {
                try
                {
                    File.Delete(convertedWavPath);
                    _logger.LogDebug("Deleted converted wav file {FilePath}", convertedWavPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error deleting converted wav file {FilePath}", convertedWavPath);
                }
            }

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

    private async Task<JsonDocument> RunWhisperXAsync(string inputFile, Job job, TranscriptionProfile profile, string outputDir, CancellationToken stoppingToken)
    {
        var arguments = BuildWhisperXArguments(inputFile, job, profile, outputDir);
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

    private async Task<JsonDocument> RunParakeetAsync(string inputFile, Job job, TranscriptionProfile profile, string outputDir, CancellationToken stoppingToken)
    {
        var arguments = BuildParakeetArguments(inputFile, profile, outputDir);
        _logger.LogDebug("Running: {Executable} {Arguments}", _options.UvxPath, arguments);

        // Get the directory containing uvx.exe
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

        // Add install directory to PATH
        if (!string.IsNullOrEmpty(installDir))
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            process.StartInfo.EnvironmentVariables["PATH"] = $"{installDir};{currentPath}";
        }

        // Set cache directories for uvx and Hugging Face models (same as whisperx)
        if (!string.IsNullOrEmpty(_options.CacheDirectory))
        {
            process.StartInfo.EnvironmentVariables["UV_CACHE_DIR"] = _options.CacheDirectory;
            process.StartInfo.EnvironmentVariables["HF_HOME"] = Path.Combine(_options.CacheDirectory, "huggingface");
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        // Regex patterns for progress parsing
        var segmentCountPattern = new System.Text.RegularExpressions.Regex(@"Found (\d+) speech segments");
        var processingPattern = new System.Text.RegularExpressions.Regex(@"Processing segment (\d+)/(\d+):");

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stdout.AppendLine(e.Data);
                _logger.LogDebug("[parakeet] {Output}", e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderr.AppendLine(e.Data);
                _logger.LogDebug("[parakeet:err] {Output}", e.Data);

                // Parse progress from stderr
                if (e.Data.Contains("Running Silero VAD"))
                {
                    job.ProgressStage = "vad";
                    job.ProgressCurrent = null;
                    job.ProgressTotal = null;
                }
                else if (e.Data.Contains("Loading model:"))
                {
                    job.ProgressStage = "loading";
                }
                else
                {
                    var segmentMatch = segmentCountPattern.Match(e.Data);
                    if (segmentMatch.Success)
                    {
                        job.ProgressTotal = int.Parse(segmentMatch.Groups[1].Value);
                    }

                    var processingMatch = processingPattern.Match(e.Data);
                    if (processingMatch.Success)
                    {
                        job.ProgressStage = "transcribing";
                        job.ProgressCurrent = int.Parse(processingMatch.Groups[1].Value);
                        job.ProgressTotal = int.Parse(processingMatch.Groups[2].Value);
                    }
                }
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
                _logger.LogWarning("Cancellation requested, killing parakeet process");
                process.Kill(entireProcessTree: true);
                throw new OperationCanceledException();
            }
            await Task.Delay(100, CancellationToken.None);
        }

        await process.WaitForExitAsync(CancellationToken.None);

        if (process.ExitCode != 0)
        {
            throw new Exception($"parakeet exited with code {process.ExitCode}: {stderr}");
        }

        // Find and parse the JSON output file
        var jsonFiles = Directory.GetFiles(outputDir, "*.json");
        if (jsonFiles.Length == 0)
        {
            throw new Exception("parakeet did not produce JSON output");
        }

        var jsonContent = await File.ReadAllTextAsync(jsonFiles[0], stoppingToken);
        return JsonDocument.Parse(jsonContent);
    }

    private async Task ConvertFlacToWavAsync(string inputPath, string outputPath, CancellationToken stoppingToken)
    {
        var installDir = Path.GetDirectoryName(_options.UvxPath) ?? "";
        var ffmpegPath = Path.Combine(installDir, "ffmpeg.exe");

        if (!File.Exists(ffmpegPath))
        {
            ffmpegPath = "ffmpeg"; // Fall back to PATH
        }

        _logger.LogInformation("Converting {Input} to 16kHz mono WAV using ffmpeg", inputPath);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-i \"{inputPath}\" -ar 16000 -ac 1 -y \"{outputPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderr.AppendLine(e.Data);
                _logger.LogDebug("[ffmpeg] {Output}", e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        while (!process.HasExited)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning("Cancellation requested, killing ffmpeg process");
                process.Kill(entireProcessTree: true);
                throw new OperationCanceledException();
            }
            await Task.Delay(100, CancellationToken.None);
        }

        await process.WaitForExitAsync(CancellationToken.None);

        if (process.ExitCode != 0)
        {
            throw new Exception($"ffmpeg exited with code {process.ExitCode}: {stderr}");
        }

        _logger.LogInformation("Successfully converted to WAV: {Output}", outputPath);
    }

    private string BuildWhisperXArguments(string inputFile, Job job, TranscriptionProfile profile, string outputDir)
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

        // Temperature: job override takes precedence, then profile default
        var temperature = job.Temperature ?? profile.Temperature;
        if (temperature.HasValue)
        {
            args.Add($"--temperature {temperature.Value}");
        }

        // Initial prompt: job override takes precedence, then profile default
        var initialPrompt = job.InitialPrompt ?? profile.InitialPrompt;
        if (!string.IsNullOrEmpty(initialPrompt))
        {
            args.Add($"--initial_prompt \"{initialPrompt}\"");
        }

        return string.Join(" ", args);
    }

    private string BuildParakeetArguments(string inputFile, TranscriptionProfile profile, string outputDir)
    {
        var args = new List<string>();

        // Use torch-backend for automatic CUDA detection
        args.Add($"--torch-backend {_options.TorchBackend}");

        // Use system Python 3.11 which includes development headers for C extension compilation
        // (UV's managed Python lacks Python.h needed by editdistance/texterrors)
        args.Add("--python-preference system");
        args.Add("--python 3.11");

        // Install required packages and run the script
        // Requires Visual C++ Build Tools on Windows for editdistance/texterrors compilation
        // Use >=2.3.0 for Windows SIGKILL fix, <2.6.0 to avoid Lhotse dataloader issues
        // torchaudio required for Silero VAD
        args.Add("--with \"nemo_toolkit[asr]>=2.3.0,<2.6.0,librosa,soundfile,torchaudio\"");
        args.Add("python");

        // Resolve the script path (relative to uvx.exe directory)
        var installDir = Path.GetDirectoryName(_options.UvxPath) ?? "";
        var scriptPath = Path.Combine(installDir, _options.ParakeetScriptPath);
        args.Add($"\"{scriptPath}\"");

        // Parakeet script arguments
        args.Add($"\"{inputFile}\"");
        args.Add($"--output_dir \"{outputDir}\"");
        args.Add($"--model {profile.ParakeetModel}");
        args.Add($"--language {profile.Language}");

        return string.Join(" ", args);
    }
}
