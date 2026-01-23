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


# Segmentation parameters (for building output segments from words)
GAP_THRESHOLD = 0.4  # 400ms pause triggers new segment at word boundary
MAX_SEGMENT_DURATION = 10.0  # Split segments longer than 10 seconds

# VAD parameters
MIN_SPEECH_DURATION_MS = 250  # Minimum speech segment duration
MIN_SILENCE_DURATION_MS = 100  # Minimum silence to split segments

# VAD chunk merging defaults (can be overridden via CLI args)
DEFAULT_VAD_MERGE_GAP = 10.0  # Merge segments with gaps < 10 seconds
DEFAULT_VAD_MAX_CHUNK = 300.0  # Maximum chunk duration (5 minutes)
DEFAULT_VAD_SPLIT_GAP = 0.5  # Minimum gap to split at when exceeding max (500ms)


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


def merge_vad_segments(speech_segments, sr, merge_gap=DEFAULT_VAD_MERGE_GAP,
                       max_chunk=DEFAULT_VAD_MAX_CHUNK, split_gap=DEFAULT_VAD_SPLIT_GAP):
    """Merge VAD segments into larger chunks for more efficient processing.

    Args:
        speech_segments: List of dicts with 'start' and 'end' in samples
        sr: Sample rate (16000)
        merge_gap: Merge segments if gap between them is less than this (seconds)
        max_chunk: Maximum chunk duration (seconds)
        split_gap: Minimum gap to split at when chunk exceeds max_chunk (seconds)

    Returns:
        List of dicts with 'start' and 'end' in samples, representing merged chunks
    """
    if not speech_segments:
        return []

    merge_gap_samples = int(merge_gap * sr)
    max_chunk_samples = int(max_chunk * sr)
    split_gap_samples = int(split_gap * sr)

    # First pass: merge segments with gaps < merge_gap
    merged = []
    current_chunk = {'start': speech_segments[0]['start'], 'end': speech_segments[0]['end']}

    for seg in speech_segments[1:]:
        gap = seg['start'] - current_chunk['end']

        if gap < merge_gap_samples:
            # Merge: extend current chunk to include this segment
            current_chunk['end'] = seg['end']
        else:
            # Gap too large: finalize current chunk, start new one
            merged.append(current_chunk)
            current_chunk = {'start': seg['start'], 'end': seg['end']}

    merged.append(current_chunk)

    # Second pass: split chunks that exceed max_chunk at gaps >= split_gap
    # We need to go back to original segments to find split points
    final_chunks = []

    for chunk in merged:
        chunk_duration = chunk['end'] - chunk['start']

        if chunk_duration <= max_chunk_samples:
            final_chunks.append(chunk)
            continue

        # Find original segments within this chunk
        segments_in_chunk = [
            seg for seg in speech_segments
            if seg['start'] >= chunk['start'] and seg['end'] <= chunk['end']
        ]

        if len(segments_in_chunk) <= 1:
            # Single long segment, can't split - just use as is
            final_chunks.append(chunk)
            continue

        # Build sub-chunks by accumulating segments until we hit max or find a good split point
        sub_chunk_start = segments_in_chunk[0]['start']
        sub_chunk_end = segments_in_chunk[0]['end']

        for i in range(1, len(segments_in_chunk)):
            seg = segments_in_chunk[i]
            gap = seg['start'] - sub_chunk_end
            potential_duration = seg['end'] - sub_chunk_start

            # If adding this segment would exceed max AND we have a suitable gap, split here
            if potential_duration > max_chunk_samples and gap >= split_gap_samples:
                final_chunks.append({'start': sub_chunk_start, 'end': sub_chunk_end})
                sub_chunk_start = seg['start']
                sub_chunk_end = seg['end']
            else:
                # Add segment to current sub-chunk
                sub_chunk_end = seg['end']

        # Don't forget the last sub-chunk
        final_chunks.append({'start': sub_chunk_start, 'end': sub_chunk_end})

    return final_chunks


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


def transcribe_with_vad(model, audio_path, output_dir, vad_model, get_speech_timestamps,
                        vad_merge_gap=DEFAULT_VAD_MERGE_GAP, vad_max_chunk=DEFAULT_VAD_MAX_CHUNK,
                        vad_split_gap=DEFAULT_VAD_SPLIT_GAP):
    """Process audio using VAD-detected speech segments with optional merging.

    Speech segments detected by Silero VAD can be merged into larger chunks
    for more efficient processing, while still ensuring quieter speakers
    get appropriate attention.

    Args:
        model: NeMo ASR model
        audio_path: Path to input audio file
        output_dir: Directory for temporary chunk files
        vad_model: Silero VAD model
        get_speech_timestamps: Function from Silero utils
        vad_merge_gap: Merge segments with gaps less than this (seconds, default: 10)
        vad_max_chunk: Maximum chunk duration (seconds, default: 300 = 5 minutes)
        vad_split_gap: Minimum gap to split at when exceeding max (seconds, default: 0.5)

    Returns:
        dict with 'segments' list in WhisperX-compatible format
    """
    audio, sr = librosa.load(audio_path, sr=16000, mono=True)
    total_duration = len(audio) / sr
    print(f"Audio duration: {total_duration/60:.1f} minutes ({total_duration/3600:.2f} hours)", file=sys.stderr)

    # Detect speech segments with Silero VAD
    print("Running Silero VAD...", file=sys.stderr)
    speech_segments = get_speech_segments(audio, sr, vad_model, get_speech_timestamps)
    print(f"Found {len(speech_segments)} raw VAD segments", file=sys.stderr)

    # Merge segments into chunks based on parameters
    chunks = merge_vad_segments(speech_segments, sr, vad_merge_gap, vad_max_chunk, vad_split_gap)
    print(f"Merged into {len(chunks)} chunks (merge_gap={vad_merge_gap}s, max_chunk={vad_max_chunk}s, split_gap={vad_split_gap}s)", file=sys.stderr)

    if not chunks:
        print("No speech detected!", file=sys.stderr)
        return {"segments": [], "language": "en"}

    all_words = []
    temp_dir = Path(output_dir)

    for i, chunk in enumerate(chunks):
        start_sample = chunk['start']
        end_sample = chunk['end']
        chunk_audio = audio[start_sample:end_sample]
        chunk_offset = start_sample / sr  # Time offset for this chunk

        chunk_duration = (end_sample - start_sample) / sr
        start_time = start_sample / sr
        end_time = end_sample / sr

        print(f"Processing chunk {i+1}/{len(chunks)}: {start_time:.1f}s - {end_time:.1f}s ({chunk_duration:.1f}s)", end="", flush=True, file=sys.stderr)

        # Write temp chunk
        temp_path = temp_dir / f"_chunk_{i+1}.wav"
        sf.write(str(temp_path), chunk_audio, sr)

        # Transcribe with timestamps
        output = model.transcribe([str(temp_path)], timestamps=True)

        # Handle the transcription output structure
        chunk_words = 0
        if output and len(output) > 0:
            hypothesis = output[0]

            # Get timestamps from the hypothesis
            timestamps = getattr(hypothesis, 'timestamp', None) or {}
            word_timestamps = timestamps.get('word', [])

            # Collect all word tokens with adjusted timestamps
            for w in word_timestamps:
                word = {
                    "text": w.get('word', ''),
                    "start": round(w.get('start', 0) + chunk_offset, 3),
                    "end": round(w.get('end', 0) + chunk_offset, 3),
                    "confidence": round(w.get('confidence', 1.0), 4),
                }
                all_words.append(word)
                chunk_words += 1

        # Cleanup temp file
        try:
            temp_path.unlink()
        except Exception:
            pass

        # Clear GPU memory
        torch.cuda.empty_cache()
        gc.collect()

        print(f" -> {chunk_words} words", file=sys.stderr)

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
    parser.add_argument("--vad_merge_gap", type=float, default=DEFAULT_VAD_MERGE_GAP,
                        help=f"Merge VAD segments with gaps less than this (seconds, default: {DEFAULT_VAD_MERGE_GAP})")
    parser.add_argument("--vad_max_chunk", type=float, default=DEFAULT_VAD_MAX_CHUNK,
                        help=f"Maximum chunk duration (seconds, default: {DEFAULT_VAD_MAX_CHUNK})")
    parser.add_argument("--vad_split_gap", type=float, default=DEFAULT_VAD_SPLIT_GAP,
                        help=f"Minimum gap to split at when exceeding max chunk (seconds, default: {DEFAULT_VAD_SPLIT_GAP})")
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
    result = transcribe_with_vad(
        model, args.input_file, args.output_dir, vad_model, get_speech_timestamps,
        vad_merge_gap=args.vad_merge_gap,
        vad_max_chunk=args.vad_max_chunk,
        vad_split_gap=args.vad_split_gap
    )
    result["language"] = args.language

    # Write output JSON (same naming convention as WhisperX)
    output_path = output_dir / f"{input_path.stem}.json"
    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2, ensure_ascii=False)

    print(f"Output written to: {output_path}", file=sys.stderr)


if __name__ == "__main__":
    main()
