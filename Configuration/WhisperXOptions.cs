using WhisperXApi.Models;

namespace WhisperXApi.Configuration;

public class WhisperXOptions
{
    public const string SectionName = "WhisperX";

    public string UvxPath { get; init; } = "uvx";
    public string TorchBackend { get; init; } = "auto";
    public string TempDirectory { get; init; } = @"C:\temp\whisperx-api";
    public string CacheDirectory { get; init; } = @"C:\temp\whisperx-api\cache";
    public int JobTimeoutMinutes { get; init; } = 30;
    public string? ApiKey { get; init; }

    /// <summary>
    /// Path to the Parakeet transcription Python script (relative to install directory)
    /// </summary>
    public string ParakeetScriptPath { get; init; } = @"Scripts\parakeet_transcribe.py";

    public Dictionary<string, TranscriptionProfile> Profiles { get; init; } = new()
    {
        ["default"] = new TranscriptionProfile(),
        ["large-v3"] = new TranscriptionProfile { Model = "large-v3" },
        ["large-v2"] = new TranscriptionProfile { Model = "large-v2" },
        ["distil-large-v3"] = new TranscriptionProfile { Model = "distil-large-v3" },
        ["parakeet"] = new TranscriptionProfile
        {
            Engine = "parakeet",
            ParakeetModel = "nvidia/parakeet-tdt-0.6b-v3"
        }
    };
}
