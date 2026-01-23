using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using WhisperXApi.Configuration;
using WhisperXApi.Models;

namespace WhisperXApi.Services;

public class JobManager
{
    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();
    private readonly ConcurrentQueue<Guid> _queue = new();
    private readonly WhisperXOptions _options;
    private readonly ILogger<JobManager> _logger;

    // Track the currently running process for cancellation
    private Process? _currentProcess;
    private readonly object _processLock = new();

    public JobManager(IOptions<WhisperXOptions> options, ILogger<JobManager> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Job CreateJob(string profile, string tempFilePath, double? temperature = null, string? initialPrompt = null,
                         double? vadMergeGap = null, double? vadMaxChunk = null, double? vadSplitGap = null)
    {
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Status = JobStatus.Queued,
            Profile = profile,
            TempFilePath = tempFilePath,
            CreatedAt = DateTime.UtcNow,
            LastPolledAt = DateTime.UtcNow,
            Temperature = temperature,
            InitialPrompt = initialPrompt,
            VadMergeGap = vadMergeGap,
            VadMaxChunk = vadMaxChunk,
            VadSplitGap = vadSplitGap
        };

        _jobs[job.Id] = job;
        _queue.Enqueue(job.Id);
        _logger.LogInformation("Created job {JobId} with profile {Profile}", job.Id, profile);

        return job;
    }

    public Job? GetJob(Guid id)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            job.LastPolledAt = DateTime.UtcNow;
            return job;
        }
        return null;
    }

    public bool TryDequeueJob(out Job? job)
    {
        job = null;
        while (_queue.TryDequeue(out var jobId))
        {
            if (_jobs.TryGetValue(jobId, out job) && job.Status == JobStatus.Queued)
            {
                return true;
            }
        }
        return false;
    }

    public bool DeleteJob(Guid id)
    {
        if (_jobs.TryRemove(id, out var job))
        {
            _logger.LogInformation("Deleting job {JobId}", id);

            // If this job is currently processing, kill the process
            if (job.Status == JobStatus.Processing)
            {
                KillCurrentProcess();
            }

            // Clean up temp file
            CleanupTempFile(job);

            // Dispose result if present
            job.Result?.Dispose();

            return true;
        }
        return false;
    }

    public void SetCurrentProcess(Process? process)
    {
        lock (_processLock)
        {
            _currentProcess = process;
        }
    }

    public void KillCurrentProcess()
    {
        lock (_processLock)
        {
            if (_currentProcess != null && !_currentProcess.HasExited)
            {
                _logger.LogWarning("Killing whisperx process");
                try
                {
                    _currentProcess.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error killing process");
                }
            }
        }
    }

    public IEnumerable<Job> GetAllJobs()
    {
        return _jobs.Values.OrderByDescending(j => j.CreatedAt);
    }

    public IEnumerable<Job> GetExpiredJobs()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-_options.JobTimeoutMinutes);
        return _jobs.Values.Where(j => j.LastPolledAt < cutoff);
    }

    public void CleanupTempFile(Job job)
    {
        if (!string.IsNullOrEmpty(job.TempFilePath) && File.Exists(job.TempFilePath))
        {
            try
            {
                File.Delete(job.TempFilePath);
                _logger.LogDebug("Deleted temp file {FilePath}", job.TempFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting temp file {FilePath}", job.TempFilePath);
            }
        }
    }

    public TranscriptionProfile GetProfile(string name)
    {
        return _options.Profiles.GetValueOrDefault(name) ?? _options.Profiles["default"];
    }
}
