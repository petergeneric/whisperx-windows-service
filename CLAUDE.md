# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

WhisperX Windows REST Service - A C#/.NET 8 web API that exposes WhisperX audio transcription as a Windows Service. Designed for remote GPU compute workflows: wake machine → submit jobs → poll results → shutdown.

## Build Commands

```bash
make build       # Build the project (Release config)
make publish     # Create single-file Windows executable (win-x64)
make installer   # Build NSIS installer (requires NSIS 3.x)
make download-deps  # Download uv 0.9.26 and ffmpeg 7.1
make clean       # Remove bin/ and obj/
```

Build requirements: .NET 8 SDK, NSIS 3.x (for installer only)

## Architecture

**Entry Point**: `Program.cs` - ASP.NET Core Minimal APIs with 5 endpoints (POST/GET/DELETE /jobs, POST /shutdown)

**Core Services** (all in `Services/`):
- `JobManager.cs` - Singleton managing in-memory job storage (ConcurrentDictionary) and FIFO queue (ConcurrentQueue)
- `TranscriptionWorker.cs` - BackgroundService that dequeues and processes jobs: FFmpeg for FLAC→WAV conversion, then UV runner for WhisperX
- `TimeoutCleanupService.cs` - BackgroundService that expires jobs not polled for 30 minutes

**Models** (in `Models/`):
- `Job.cs` - Job entity with status enum (Queued → Processing → Completed/Failed)
- `TranscriptionProfile.cs` - Per-model configuration (distil-large-v3.5, large-v3, etc.)

**Configuration** (in `Configuration/`):
- `WhisperXOptions.cs` - Settings model bound to `appsettings.json:WhisperX` section

## Key Patterns

- **Single-threaded FIFO processing**: Jobs queue and process one at a time
- **In-memory storage**: No database; jobs auto-expire after 30 minutes without polling
- **Process management**: WhisperX runs via `uvx --torch-backend auto whisperx [args]`
- **Windows Service integration**: Uses Microsoft.Extensions.Hosting.WindowsServices

## API Documentation

See `API.md` for complete REST API documentation including all endpoints, authentication (X-API-Key header), and response formats.

## Runtime Directories

All state stored in `C:\temp\whisperx-api\`:
- Temporary audio files
- UV and HuggingFace caches
- API key file (auto-generated if not in appsettings.json)
