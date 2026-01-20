using WhisperXApi.Models;

namespace WhisperXApi.Services;

public class TimeoutCleanupService : BackgroundService
{
    private readonly JobManager _jobManager;
    private readonly ILogger<TimeoutCleanupService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);

    public TimeoutCleanupService(JobManager jobManager, ILogger<TimeoutCleanupService> logger)
    {
        _jobManager = jobManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TimeoutCleanupService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CleanupExpiredJobs();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cleanup");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }

        _logger.LogInformation("TimeoutCleanupService stopping");
    }

    private void CleanupExpiredJobs()
    {
        var expiredJobs = _jobManager.GetExpiredJobs().ToList();

        foreach (var job in expiredJobs)
        {
            _logger.LogInformation(
                "Cleaning up expired job {JobId} (last polled: {LastPolled})",
                job.Id,
                job.LastPolledAt);

            _jobManager.DeleteJob(job.Id);
        }

        if (expiredJobs.Count > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired jobs", expiredJobs.Count);
        }
    }
}
