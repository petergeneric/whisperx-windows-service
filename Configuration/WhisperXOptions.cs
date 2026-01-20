using WhisperXApi.Models;

namespace WhisperXApi.Configuration;

public class WhisperXOptions
{
    public const string SectionName = "WhisperX";

    public string UvxPath { get; init; } = "uvx";
    public string TorchBackend { get; init; } = "auto";
    public string TempDirectory { get; init; } = @"C:\temp\whisperx-api";
    public int JobTimeoutMinutes { get; init; } = 30;
    public string? ApiKey { get; init; }
    public Dictionary<string, TranscriptionProfile> Profiles { get; init; } = new()
    {
        ["default"] = new TranscriptionProfile()
    };
}
