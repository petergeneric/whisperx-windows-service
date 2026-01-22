using System.Text.Json;

namespace WhisperXApi.Models;

public enum JobStatus
{
    Queued,
    Processing,
    Completed,
    Failed
}

public class Job
{
    public Guid Id { get; init; }
    public JobStatus Status { get; set; }
    public string Profile { get; init; } = "default";
    public string? TempFilePath { get; set; }
    public JsonDocument? Result { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastPolledAt { get; set; }
    public double? Temperature { get; init; }
    public string? InitialPrompt { get; init; }
}
