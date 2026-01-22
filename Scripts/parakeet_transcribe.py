#!/usr/bin/env python3
"""NVIDIA Parakeet transcription script with chunked processing for long audio."""
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


def finalize_segment(words):
    """Convert a list of word tokens into a segment dict."""
    text = "".join(w["text"] for w in words).strip()
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
        starts_with_space = curr_word["text"].startswith(" ")
        segment_duration = prev_word["end"] - segment_start_time

        # Break segment on:
        # 1. Pause at word boundary (gap > threshold AND starts with space)
        # 2. Duration exceeds max AND we're at a word boundary (starts with space)
        should_break = (
            (gap > gap_threshold and starts_with_space) or
            (segment_duration > max_duration and starts_with_space)
        )

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


def transcribe_in_chunks(model, audio_path, output_dir, chunk_duration=300.0, overlap=5.0):
    """Process audio in 5-minute chunks with 5-second overlap.

    Args:
        model: NeMo ASR model
        audio_path: Path to input audio file
        output_dir: Directory for temporary chunk files
        chunk_duration: Duration of each chunk in seconds (default: 300 = 5 minutes)
        overlap: Overlap between chunks in seconds (default: 5)

    Returns:
        dict with 'segments' list in WhisperX-compatible format
    """
    audio, sr = librosa.load(audio_path, sr=16000, mono=True)
    total_duration = len(audio) / sr
    print(f"Audio duration: {total_duration/60:.1f} minutes ({total_duration/3600:.2f} hours)", file=sys.stderr)

    chunk_samples = int(chunk_duration * sr)
    overlap_samples = int(overlap * sr)
    stride_samples = chunk_samples - overlap_samples

    all_words = []
    start_sample = 0
    chunk_num = 0
    temp_dir = Path(output_dir)

    while start_sample < len(audio):
        chunk_num += 1
        end_sample = min(start_sample + chunk_samples, len(audio))
        chunk = audio[start_sample:end_sample]
        chunk_offset = start_sample / sr

        # Write temp chunk
        temp_path = temp_dir / f"_chunk_{chunk_num}.wav"
        sf.write(str(temp_path), chunk, sr)

        start_time = start_sample / sr
        end_time = end_sample / sr
        print(f"Processing chunk {chunk_num}: {start_time/60:.1f} - {end_time/60:.1f} min", end="", flush=True, file=sys.stderr)

        # Transcribe with timestamps
        output = model.transcribe([str(temp_path)], timestamps=True)

        # Handle the transcription output structure
        # NeMo returns a list of hypotheses, each with timestamp info
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

        print(f" -> {chunk_words} tokens", file=sys.stderr)

        start_sample += stride_samples

    # Build segments using pause-based detection
    segments = build_segments_from_words(all_words)

    print(f"Built {len(segments)} segments from {len(all_words)} tokens", file=sys.stderr)

    return {"segments": segments, "language": "en"}


def main():
    parser = argparse.ArgumentParser(description="Transcribe audio using NVIDIA Parakeet")
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

    print(f"Loading model: {args.model}", file=sys.stderr)
    model = nemo_asr.models.ASRModel.from_pretrained(args.model)

    print(f"Transcribing: {args.input_file}", file=sys.stderr)
    result = transcribe_in_chunks(model, args.input_file, args.output_dir)
    result["language"] = args.language

    # Write output JSON (same naming convention as WhisperX)
    output_path = output_dir / f"{input_path.stem}.json"
    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2, ensure_ascii=False)

    print(f"Output written to: {output_path}", file=sys.stderr)


if __name__ == "__main__":
    main()
