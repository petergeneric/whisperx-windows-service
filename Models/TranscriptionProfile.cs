namespace WhisperXApi.Models;

public class TranscriptionProfile
{
    public string Model { get; init; } = "distil-large-v3.5";
    public string Device { get; init; } = "cuda";
    public string ComputeType { get; init; } = "float16";
    public string Language { get; init; } = "en";
    public string AlignModel { get; init; } = "WAV2VEC2_ASR_LARGE_LV60K_960H";
    public string VadMethod { get; init; } = "silero";
}
