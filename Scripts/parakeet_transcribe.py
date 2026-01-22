#!/usr/bin/env python3
"""NVIDIA Parakeet transcription script with Silero VAD for speech detection."""
import argparse
import gc
import json
import sys
from pathlib import Path

import torch
import librosa
import soundfile as sf
import nemo.collections.asr as nemo_asr


# Segmentation parameters
GAP_THRESHOLD = 0.4  # 400ms pause triggers new segment at word boundary
MAX_SEGMENT_DURATION = 10.0  # Split segments longer than 10 seconds

# VAD parameters
MIN_SPEECH_DURATION_MS = 250  # Minimum speech segment duration
MIN_SILENCE_DURATION_MS = 100  # Minimum silence to split segments


def load_silero_vad():
    """Load Silero VAD model from torch hub."""
    model, utils = torch.hub.load(
        repo_or_dir='snakers4/silero-vad',
        model='silero_vad',
        trust_repo=True,
        verbose=False
    )
    get_speech_timestamps = utils[0]
    return model, get_speech_timestamps


def get_speech_segments(audio, sr, vad_model, get_speech_timestamps):
    """Use Silero VAD to detect speech segments.

    Args:
        audio: Audio as numpy array
        sr: Sample rate (should be 16000)
        vad_model: Silero VAD model
        get_speech_timestamps: Function from Silero utils

    Returns:
        List of dicts with 'start' and 'end' in samples
    """
    # Convert to tensor
    audio_tensor = torch.from_numpy(audio).float()

    # Get speech timestamps
    speech_timestamps = get_speech_timestamps(
        audio_tensor,
        vad_model,
        sampling_rate=sr,
        min_speech_duration_ms=MIN_SPEECH_DURATION_MS,
        min_silence_duration_ms=MIN_SILENCE_DURATION_MS,
    )

    return speech_timestamps


def finalize_segment(words):
    """Convert a list of word tokens into a segment dict."""
    text = " ".join(w["text"].strip() for w in words)
    # Convert internal word format to WhisperX-compatible format
    whisperx_words = [
        {
            "word": w["text"].strip(),
            "start": w["start"],
            "end": w["end"],
            "score": w.get("confidence", 1.0),
        }
        for w in words
        if w["text"].strip()  # Skip empty tokens
    ]
    return {
        "start": words[0]["start"],
        "end": words[-1]["end"],
        "text": text,
        "words": whisperx_words,
    }


def build_segments_from_words(all_words, gap_threshold=GAP_THRESHOLD, max_duration=MAX_SEGMENT_DURATION):
    """Build segments by detecting pauses >gap_threshold seconds at word boundaries.

    Also splits segments that exceed max_duration seconds at the next word boundary.

    Args:
        all_words: List of word dicts with 'text', 'start', 'end' keys
        gap_threshold: Pause duration in seconds that triggers a new segment (default: 0.4)
        max_duration: Maximum segment duration before forcing a split (default: 10.0)

    Returns:
        List of segment dicts with 'start', 'end', 'text', 'words' keys
    """
    if not all_words:
        return []

    segments = []
    current_segment_words = [all_words[0]]
    segment_start_time = all_words[0]["start"]

    for i in range(1, len(all_words)):
        prev_word = all_words[i - 1]
        curr_word = all_words[i]

        gap = curr_word["start"] - prev_word["end"]
        segment_duration = prev_word["end"] - segment_start_time

        # Break segment on:
        # 1. Pause exceeds gap threshold (400ms default)
        # 2. Duration exceeds max (10s default)
        # Note: NeMo returns individual words (not subword tokens), so every word is a valid break point
        should_break = (gap > gap_threshold) or (segment_duration > max_duration)

        if should_break:
            # Finalize current segment
            segments.append(finalize_segment(current_segment_words))
            current_segment_words = [curr_word]
            segment_start_time = curr_word["start"]
        else:
            current_segment_words.append(curr_word)

    # Don't forget the last segment
    if current_segment_words:
        segments.append(finalize_segment(current_segment_words))

    return segments


def transcribe_with_vad(model, audio_path, output_dir, vad_model, get_speech_timestamps):
    """Process audio using VAD-detected speech segments.

    Each speech segment detected by Silero VAD is transcribed individually,
    ensuring quieter speech gets full model attention.

    Args:
        model: NeMo ASR model
        audio_path: Path to input audio file
        output_dir: Directory for temporary chunk files
        vad_model: Silero VAD model
        get_speech_timestamps: Function from Silero utils

    Returns:
        dict with 'segments' list in WhisperX-compatible format
    """
    audio, sr = librosa.load(audio_path, sr=16000, mono=True)
    total_duration = len(audio) / sr
    print(f"Audio duration: {total_duration/60:.1f} minutes ({total_duration/3600:.2f} hours)", file=sys.stderr)

    # Detect speech segments with Silero VAD
    print("Running Silero VAD...", file=sys.stderr)
    speech_segments = get_speech_segments(audio, sr, vad_model, get_speech_timestamps)
    print(f"Found {len(speech_segments)} speech segments", file=sys.stderr)

    if not speech_segments:
        print("No speech detected!", file=sys.stderr)
        return {"segments": [], "language": "en"}

    all_words = []
    temp_dir = Path(output_dir)

    for i, seg in enumerate(speech_segments):
        start_sample = seg['start']
        end_sample = seg['end']
        segment_audio = audio[start_sample:end_sample]
        segment_offset = start_sample / sr  # Time offset for this segment

        segment_duration = (end_sample - start_sample) / sr
        start_time = start_sample / sr
        end_time = end_sample / sr

        print(f"Processing segment {i+1}/{len(speech_segments)}: {start_time:.1f}s - {end_time:.1f}s ({segment_duration:.1f}s)", end="", flush=True, file=sys.stderr)

        # Write temp segment
        temp_path = temp_dir / f"_segment_{i+1}.wav"
        sf.write(str(temp_path), segment_audio, sr)

        # Transcribe with timestamps
        output = model.transcribe([str(temp_path)], timestamps=True)

        # Handle the transcription output structure
        segment_words = 0
        if output and len(output) > 0:
            hypothesis = output[0]

            # Get timestamps from the hypothesis
            timestamps = getattr(hypothesis, 'timestamp', None) or {}
            word_timestamps = timestamps.get('word', [])

            # Collect all word tokens with adjusted timestamps
            for w in word_timestamps:
                word = {
                    "text": w.get('word', ''),
                    "start": round(w.get('start', 0) + segment_offset, 3),
                    "end": round(w.get('end', 0) + segment_offset, 3),
                    "confidence": round(w.get('confidence', 1.0), 4),
                }
                all_words.append(word)
                segment_words += 1

        # Cleanup temp file
        try:
            temp_path.unlink()
        except Exception:
            pass

        # Clear GPU memory
        torch.cuda.empty_cache()
        gc.collect()

        print(f" -> {segment_words} words", file=sys.stderr)

    # Build segments using pause-based detection
    segments = build_segments_from_words(all_words)

    print(f"Built {len(segments)} output segments from {len(all_words)} words", file=sys.stderr)

    return {"segments": segments, "language": "en"}


def main():
    parser = argparse.ArgumentParser(description="Transcribe audio using NVIDIA Parakeet with Silero VAD")
    parser.add_argument("input_file", help="Path to input audio file")
    parser.add_argument("--output_dir", required=True, help="Directory for output JSON")
    parser.add_argument("--model", default="nvidia/parakeet-tdt-0.6b-v3",
                        help="Parakeet model name (default: nvidia/parakeet-tdt-0.6b-v3)")
    parser.add_argument("--language", default="en", help="Language code (default: en)")
    args = parser.parse_args()

    input_path = Path(args.input_file)
    if not input_path.exists():
        print(f"Error: Input file not found: {args.input_file}", file=sys.stderr)
        sys.exit(1)

    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    print("Loading Silero VAD...", file=sys.stderr)
    vad_model, get_speech_timestamps = load_silero_vad()

    print(f"Loading model: {args.model}", file=sys.stderr)
    model = nemo_asr.models.ASRModel.from_pretrained(args.model)

    print(f"Transcribing: {args.input_file}", file=sys.stderr)
    result = transcribe_with_vad(model, args.input_file, args.output_dir, vad_model, get_speech_timestamps)
    result["language"] = args.language

    # Write output JSON (same naming convention as WhisperX)
    output_path = output_dir / f"{input_path.stem}.json"
    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2, ensure_ascii=False)

    print(f"Output written to: {output_path}", file=sys.stderr)


if __name__ == "__main__":
    main()
