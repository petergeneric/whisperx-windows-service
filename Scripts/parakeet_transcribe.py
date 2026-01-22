#!/usr/bin/env python3
"""NVIDIA Parakeet transcription script with chunked processing for long audio."""
import argparse
import gc
import json
import os
import sys
from pathlib import Path

import torch
import librosa
import soundfile as sf
import nemo.collections.asr as nemo_asr


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

    chunk_samples = int(chunk_duration * sr)
    overlap_samples = int(overlap * sr)
    stride_samples = chunk_samples - overlap_samples

    all_segments = []
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

        print(f"Processing chunk {chunk_num} (offset {chunk_offset:.1f}s)...", file=sys.stderr)

        # Transcribe with timestamps
        output = model.transcribe([str(temp_path)], timestamps=True)

        # Handle the transcription output structure
        # NeMo returns a list of hypotheses, each with timestamp info
        if output and len(output) > 0:
            hypothesis = output[0]

            # Get timestamps from the hypothesis
            timestamps = getattr(hypothesis, 'timestamp', None) or {}
            segment_timestamps = timestamps.get('segment', [])
            word_timestamps = timestamps.get('word', [])

            # Process each segment
            for seg in segment_timestamps:
                # Find words that belong to this segment
                seg_start = seg.get('start', 0)
                seg_end = seg.get('end', 0)
                seg_text = seg.get('segment', '').strip()

                words = []
                for w in word_timestamps:
                    w_start = w.get('start', 0)
                    w_end = w.get('end', 0)
                    if seg_start <= w_start < seg_end:
                        words.append({
                            "word": w.get('word', ''),
                            "start": round(w_start + chunk_offset, 3),
                            "end": round(w_end + chunk_offset, 3),
                            "score": round(w.get('confidence', 1.0), 4)
                        })

                all_segments.append({
                    "start": round(seg_start + chunk_offset, 3),
                    "end": round(seg_end + chunk_offset, 3),
                    "text": seg_text,
                    "words": words
                })

        # Cleanup temp file
        try:
            temp_path.unlink()
        except Exception:
            pass

        # Clear GPU memory
        torch.cuda.empty_cache()
        gc.collect()

        start_sample += stride_samples

    return {"segments": all_segments, "language": "en"}


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
