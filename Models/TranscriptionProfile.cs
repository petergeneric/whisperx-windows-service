namespace WhisperXApi.Models;

public class TranscriptionProfile
{
    /// <summary>
    /// Engine type: "whisperx" (default) or "parakeet"
    /// </summary>
    public string Engine { get; init; } = "whisperx";

    // WhisperX-specific properties
    public string Model { get; init; } = "distil-large-v3.5";
    public string Device { get; init; } = "cuda";
    public string ComputeType { get; init; } = "float16";
    public string Language { get; init; } = "en";
    public string AlignModel { get; init; } = "WAV2VEC2_ASR_LARGE_LV60K_960H";
    public string VadMethod { get; init; } = "silero";
    public double? Temperature { get; init; }
    public string? InitialPrompt { get; init; }

    /// <summary>
    /// Parakeet model name (used when Engine = "parakeet")
    /// </summary>
    public string ParakeetModel { get; init; } = "nvidia/parakeet-tdt-0.6b-v3";
}
