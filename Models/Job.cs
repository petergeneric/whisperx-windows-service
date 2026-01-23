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

    // VAD chunking parameters (for Parakeet jobs)
    public double? VadMergeGap { get; init; }   // Merge segments with gaps less than this (seconds)
    public double? VadMaxChunk { get; init; }   // Maximum chunk duration (seconds)
    public double? VadSplitGap { get; init; }   // Minimum gap to split at when exceeding max (seconds)

    // Progress tracking (for Parakeet jobs)
    public string? ProgressStage { get; set; }  // "vad" or "transcribing"
    public int? ProgressCurrent { get; set; }   // Current segment number
    public int? ProgressTotal { get; set; }     // Total segments
}
