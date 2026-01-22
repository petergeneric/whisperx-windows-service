# WhisperX API Documentation

REST API for audio transcription using WhisperX with CUDA acceleration.

## Base URL

```
http://<host>:5173
```

Default: `http://localhost:5173` (local) or `http://<machine-ip>:5173` (network)

---

## Authentication

All API endpoints require authentication via the `X-API-Key` header.

```
X-API-Key: <your-api-key>
```

### API Key Location

The API key is auto-generated on first startup and saved to:
```
{TempDirectory}/api-key.txt
```

Default: `C:\temp\whisperx-api\api-key.txt`

Alternatively, you can set a custom key in `appsettings.json`:
```json
{
  "WhisperX": {
    "ApiKey": "your-custom-key-here"
  }
}
```

### Unauthorized Response

All endpoints return **401 Unauthorized** (no body) if the API key is missing or invalid.

---

## Endpoints

### Create Transcription Job

Start a new transcription job by uploading an audio file.

```
POST /jobs
Content-Type: multipart/form-data
X-API-Key: <api-key>
```

#### Request

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `file` | File | Yes | Audio file (`.wav` or `.flac`) |
| `profile` | String | No | Transcription profile name (default: `"default"`) |
| `temperature` | Number | No | Sampling temperature for transcription (overrides profile default) |
| `initial_prompt` | String | No | Initial prompt to condition the transcription (overrides profile default) |

#### Response

**201 Created**
```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000"
}
```

**400 Bad Request**
```json
{
  "error": "No file provided"
}
```
```json
{
  "error": "File must be .wav or .flac"
}
```
```json
{
  "error": "Unknown profile: custom"
}
```

#### Electron Example

```javascript
async function createTranscriptionJob(apiKey, filePath, profile = 'default') {
  const fs = require('fs');
  const path = require('path');
  const FormData = require('form-data');

  const form = new FormData();
  form.append('file', fs.createReadStream(filePath));
  form.append('profile', profile);

  const response = await fetch('http://localhost:5173/jobs', {
    method: 'POST',
    body: form,
    headers: {
      ...form.getHeaders(),
      'X-API-Key': apiKey,
    },
  });

  if (!response.ok) {
    const error = await response.json();
    throw new Error(error.error);
  }

  return response.json(); // { id: "..." }
}
```

Using `electron-fetch` or Node.js 18+ built-in fetch:

```javascript
async function createTranscriptionJob(apiKey, filePath, profile = 'default') {
  const fs = require('fs');
  const path = require('path');

  const fileBuffer = fs.readFileSync(filePath);
  const fileName = path.basename(filePath);

  const formData = new FormData();
  formData.append('file', new Blob([fileBuffer]), fileName);
  formData.append('profile', profile);

  const response = await fetch('http://localhost:5173/jobs', {
    method: 'POST',
    body: formData,
    headers: {
      'X-API-Key': apiKey,
    },
  });

  if (!response.ok) {
    const error = await response.json();
    throw new Error(error.error);
  }

  return response.json();
}
```

---

### List All Jobs

Get a list of all jobs in memory.

```
GET /jobs
X-API-Key: <api-key>
```

#### Response

**200 OK**
```json
{
  "jobs": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "status": "completed",
      "profile": "default",
      "createdAt": "2024-01-15T10:30:00Z",
      "error": null
    },
    {
      "id": "660e8400-e29b-41d4-a716-446655440001",
      "status": "processing",
      "profile": "fast",
      "createdAt": "2024-01-15T10:35:00Z",
      "error": null
    }
  ]
}
```

#### Electron Example

```javascript
async function listJobs(apiKey) {
  const response = await fetch('http://localhost:5173/jobs', {
    headers: { 'X-API-Key': apiKey },
  });
  const { jobs } = await response.json();
  return jobs;
}
```

---

### Get Job Status

Poll for job status and retrieve results when complete.

```
GET /jobs/{id}
X-API-Key: <api-key>
```

#### Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | UUID | Job ID returned from POST /jobs |

#### Response

**200 OK - Queued**
```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "status": "queued"
}
```

**200 OK - Processing**
```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "status": "processing",
  "progress": null
}
```

**200 OK - Processing (Parakeet with progress)**
```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "status": "processing",
  "progress": {
    "stage": "transcribing",
    "current": 42,
    "total": 847
  }
}
```

Progress stages for Parakeet jobs:
- `"vad"` - Running Silero VAD speech detection
- `"loading"` - Loading Parakeet model
- `"transcribing"` - Processing speech segments (includes `current`/`total`)

**200 OK - Completed**
```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "status": "completed",
  "result": {
    "segments": [
      {
        "start": 0.0,
        "end": 2.5,
        "text": "Hello world.",
        "words": [
          { "word": "Hello", "start": 0.0, "end": 0.5, "score": 0.95 },
          { "word": "world.", "start": 0.6, "end": 1.0, "score": 0.92 }
        ]
      }
    ],
    "language": "en"
  }
}
```

**200 OK - Failed**
```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "status": "failed",
  "error": "whisperx exited with code 1: CUDA out of memory"
}
```

**404 Not Found**
```json
{
  "error": "Job not found"
}
```

#### Electron Example

```javascript
async function getJobStatus(apiKey, jobId) {
  const response = await fetch(`http://localhost:5173/jobs/${jobId}`, {
    headers: { 'X-API-Key': apiKey },
  });

  if (!response.ok) {
    if (response.status === 404) {
      throw new Error('Job not found');
    }
    throw new Error('Failed to get job status');
  }

  return response.json();
}
```

---

### Delete Job

Cancel a running job or remove a completed job from memory.

```
DELETE /jobs/{id}
X-API-Key: <api-key>
```

#### Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `id` | UUID | Job ID |

#### Response

**204 No Content** - Job deleted successfully

**404 Not Found**
```json
{
  "error": "Job not found"
}
```

#### Behavior

- If the job is **queued**: Removes from queue
- If the job is **processing**: Kills the whisperx process, then removes
- If the job is **completed/failed**: Removes from memory

#### Electron Example

```javascript
async function deleteJob(apiKey, jobId) {
  const response = await fetch(`http://localhost:5173/jobs/${jobId}`, {
    method: 'DELETE',
    headers: { 'X-API-Key': apiKey },
  });

  if (!response.ok && response.status !== 204) {
    if (response.status === 404) {
      throw new Error('Job not found');
    }
    throw new Error('Failed to delete job');
  }

  return true;
}
```

---

### Shutdown Machine

Initiate a full machine shutdown.

```
POST /shutdown
X-API-Key: <api-key>
```

#### Response

**200 OK**
```json
{
  "message": "Machine shutdown initiated"
}
```

#### Behavior

- Schedules a machine shutdown with a 5-second delay (allows response to be sent)
- Gracefully stops the API service
- The machine powers off after 5 seconds

#### Use Case

This endpoint is designed for remote GPU compute workflows:
1. Wake-on-LAN to boot the machine
2. Submit transcription jobs via the API
3. Poll until all jobs complete
4. Call `/shutdown` to power off the machine

#### Electron Example

```javascript
async function shutdownMachine(apiKey) {
  const response = await fetch('http://localhost:5173/shutdown', {
    method: 'POST',
    headers: { 'X-API-Key': apiKey },
  });

  if (!response.ok) {
    throw new Error('Failed to initiate shutdown');
  }

  return response.json(); // { message: "Machine shutdown initiated" }
}
```

#### cURL Example

```bash
# Read API key and shutdown
curl -X POST http://localhost:5173/shutdown \
  -H "X-API-Key: $(cat /c/temp/whisperx-api/api-key.txt)"
```

---

## Complete Electron Integration

### TranscriptionService Class

```javascript
const fs = require('fs');
const path = require('path');
const EventEmitter = require('events');

class TranscriptionService extends EventEmitter {
  constructor(apiKey, baseUrl = 'http://localhost:5173') {
    super();
    this.apiKey = apiKey;
    this.baseUrl = baseUrl;
    this.pollingInterval = 1000; // 1 second
    this.activePolls = new Map();
  }

  /**
   * Submit an audio file for transcription
   * @param {string} filePath - Path to .wav or .flac file
   * @param {string} profile - Transcription profile (default: 'default')
   * @returns {Promise<string>} Job ID
   */
  async submitJob(filePath, profile = 'default') {
    const fileBuffer = fs.readFileSync(filePath);
    const fileName = path.basename(filePath);

    const formData = new FormData();
    formData.append('file', new Blob([fileBuffer]), fileName);
    formData.append('profile', profile);

    const response = await fetch(`${this.baseUrl}/jobs`, {
      method: 'POST',
      body: formData,
      headers: { 'X-API-Key': this.apiKey },
    });

    if (!response.ok) {
      const error = await response.json();
      throw new Error(error.error);
    }

    const { id } = await response.json();
    return id;
  }

  /**
   * Get current job status
   * @param {string} jobId
   * @returns {Promise<Object>} Job status object
   */
  async getStatus(jobId) {
    const response = await fetch(`${this.baseUrl}/jobs/${jobId}`, {
      headers: { 'X-API-Key': this.apiKey },
    });

    if (!response.ok) {
      if (response.status === 404) {
        throw new Error('Job not found');
      }
      throw new Error('Failed to get job status');
    }

    return response.json();
  }

  /**
   * Cancel/delete a job
   * @param {string} jobId
   */
  async cancelJob(jobId) {
    // Stop polling if active
    this.stopPolling(jobId);

    const response = await fetch(`${this.baseUrl}/jobs/${jobId}`, {
      method: 'DELETE',
      headers: { 'X-API-Key': this.apiKey },
    });

    if (!response.ok && response.status !== 204 && response.status !== 404) {
      throw new Error('Failed to cancel job');
    }
  }

  /**
   * Submit job and poll until completion
   * @param {string} filePath
   * @param {string} profile
   * @returns {Promise<Object>} Transcription result
   */
  async transcribe(filePath, profile = 'default') {
    const jobId = await this.submitJob(filePath, profile);
    return this.waitForCompletion(jobId);
  }

  /**
   * Poll for job completion
   * @param {string} jobId
   * @returns {Promise<Object>} Transcription result
   */
  waitForCompletion(jobId) {
    return new Promise((resolve, reject) => {
      const poll = async () => {
        try {
          const status = await this.getStatus(jobId);
          this.emit('status', { jobId, ...status });

          if (status.status === 'completed') {
            this.stopPolling(jobId);
            resolve(status.result);
          } else if (status.status === 'failed') {
            this.stopPolling(jobId);
            reject(new Error(status.error));
          }
          // Continue polling for 'queued' and 'processing'
        } catch (error) {
          this.stopPolling(jobId);
          reject(error);
        }
      };

      // Start polling
      poll();
      const intervalId = setInterval(poll, this.pollingInterval);
      this.activePolls.set(jobId, intervalId);
    });
  }

  /**
   * Stop polling for a job
   * @param {string} jobId
   */
  stopPolling(jobId) {
    const intervalId = this.activePolls.get(jobId);
    if (intervalId) {
      clearInterval(intervalId);
      this.activePolls.delete(jobId);
    }
  }

  /**
   * Check if API is reachable
   * @returns {Promise<boolean>}
   */
  async isAvailable() {
    try {
      const response = await fetch(`${this.baseUrl}/jobs/00000000-0000-0000-0000-000000000000`, {
        headers: { 'X-API-Key': this.apiKey },
      });
      // 404 means API is up, just job doesn't exist
      return response.status === 404 || response.ok;
    } catch {
      return false;
    }
  }
}

module.exports = TranscriptionService;
```

### Usage in Electron Main Process

```javascript
const { app, ipcMain } = require('electron');
const fs = require('fs');
const TranscriptionService = require('./TranscriptionService');

// Load API key from file
const apiKey = fs.readFileSync('C:\\temp\\whisperx-api\\api-key.txt', 'utf-8').trim();
const transcription = new TranscriptionService(apiKey, 'http://localhost:5173');

// Check API availability on startup
app.whenReady().then(async () => {
  const available = await transcription.isAvailable();
  if (!available) {
    console.error('WhisperX API is not available');
  }
});

// Handle transcription requests from renderer
ipcMain.handle('transcribe', async (event, filePath, profile) => {
  const jobId = await transcription.submitJob(filePath, profile);
  return jobId;
});

ipcMain.handle('transcribe-status', async (event, jobId) => {
  return transcription.getStatus(jobId);
});

ipcMain.handle('transcribe-cancel', async (event, jobId) => {
  await transcription.cancelJob(jobId);
});

// Simple one-shot transcription
ipcMain.handle('transcribe-file', async (event, filePath, profile) => {
  return transcription.transcribe(filePath, profile);
});
```

### Usage in Electron Renderer Process

```javascript
// In preload.js
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('transcription', {
  submit: (filePath, profile) => ipcRenderer.invoke('transcribe', filePath, profile),
  getStatus: (jobId) => ipcRenderer.invoke('transcribe-status', jobId),
  cancel: (jobId) => ipcRenderer.invoke('transcribe-cancel', jobId),
  transcribe: (filePath, profile) => ipcRenderer.invoke('transcribe-file', filePath, profile),
});

// In renderer
async function handleFileUpload(filePath) {
  try {
    // Simple: wait for full result
    const result = await window.transcription.transcribe(filePath);
    console.log('Transcription:', result);

    // Or: manual polling with progress
    const jobId = await window.transcription.submit(filePath);
    pollForResult(jobId);
  } catch (error) {
    console.error('Transcription failed:', error.message);
  }
}

async function pollForResult(jobId) {
  const status = await window.transcription.getStatus(jobId);

  switch (status.status) {
    case 'queued':
      updateUI('Waiting in queue...');
      setTimeout(() => pollForResult(jobId), 1000);
      break;
    case 'processing':
      // Show progress for Parakeet jobs
      if (status.progress) {
        const { stage, current, total } = status.progress;
        if (stage === 'vad') {
          updateUI('Detecting speech...');
        } else if (stage === 'loading') {
          updateUI('Loading model...');
        } else if (stage === 'transcribing' && current && total) {
          updateUI(`Transcribing segment ${current}/${total}...`);
        } else {
          updateUI('Processing...');
        }
      } else {
        updateUI('Transcribing...');
      }
      setTimeout(() => pollForResult(jobId), 1000);
      break;
    case 'completed':
      updateUI('Done!');
      handleResult(status.result);
      break;
    case 'failed':
      updateUI('Failed: ' + status.error);
      break;
  }
}
```

---

## WhisperX Result Format

The `result` field in a completed job contains the WhisperX JSON output:

```json
{
  "segments": [
    {
      "start": 0.0,
      "end": 4.2,
      "text": " This is the transcribed text.",
      "words": [
        {
          "word": "This",
          "start": 0.0,
          "end": 0.3,
          "score": 0.98
        },
        {
          "word": "is",
          "start": 0.35,
          "end": 0.5,
          "score": 0.95
        }
      ]
    }
  ],
  "language": "en"
}
```

### Segment Fields

| Field | Type | Description |
|-------|------|-------------|
| `start` | float | Start time in seconds |
| `end` | float | End time in seconds |
| `text` | string | Transcribed text for segment |
| `words` | array | Word-level timestamps (if alignment enabled) |

### Word Fields

| Field | Type | Description |
|-------|------|-------------|
| `word` | string | The word |
| `start` | float | Start time in seconds |
| `end` | float | End time in seconds |
| `score` | float | Confidence score (0-1) |

---

## Job Lifecycle

```
┌─────────┐     ┌────────────┐     ┌───────────┐
│ Queued  │────▶│ Processing │────▶│ Completed │
└─────────┘     └────────────┘     └───────────┘
                      │
                      │ (error)
                      ▼
                ┌──────────┐
                │  Failed  │
                └──────────┘
```

- Jobs are processed **one at a time** (FIFO queue)
- Jobs **expire after 30 minutes** if not polled
- Completed jobs remain in memory until deleted or expired

---

## Error Handling

| HTTP Status | Meaning |
|-------------|---------|
| 200 | Success |
| 201 | Job created |
| 204 | Job deleted (no content) |
| 400 | Bad request (invalid file, unknown profile) |
| 401 | Unauthorized (missing or invalid API key for /shutdown) |
| 404 | Job not found |
| 500 | Internal server error |

---

## Configuration

The API can be configured via `appsettings.json`:

```json
{
  "WhisperX": {
    "UvxPath": "uvx",
    "TorchBackend": "auto",
    "TempDirectory": "C:\\temp\\whisperx-api",
    "CacheDirectory": "C:\\temp\\whisperx-api\\cache",
    "JobTimeoutMinutes": 30,
    "ParakeetScriptPath": "Scripts\\parakeet_transcribe.py",
    "Profiles": {
      "default": {
        "Engine": "whisperx",
        "Model": "distil-large-v3.5",
        "Device": "cuda",
        "ComputeType": "float16",
        "Language": "en",
        "AlignModel": "WAV2VEC2_ASR_LARGE_LV60K_960H",
        "VadMethod": "silero"
      },
      "large-v3": {
        "Engine": "whisperx",
        "Model": "large-v3"
      },
      "large-v2": {
        "Engine": "whisperx",
        "Model": "large-v2"
      },
      "distil-large-v3": {
        "Engine": "whisperx",
        "Model": "distil-large-v3"
      },
      "parakeet": {
        "Engine": "parakeet",
        "ParakeetModel": "nvidia/parakeet-tdt-0.6b-v3",
        "Language": "en"
      }
    }
  },
  "Urls": "http://0.0.0.0:5173"
}
```

### Built-in Profiles

| Profile | Engine | Model | Description |
|---------|--------|-------|-------------|
| `default` | `whisperx` | `distil-large-v3.5` | Fast distilled model, good balance of speed and accuracy |
| `large-v3` | `whisperx` | `large-v3` | Full model, best accuracy |
| `large-v2` | `whisperx` | `large-v2` | Previous full model version |
| `distil-large-v3` | `whisperx` | `distil-large-v3` | Distilled v3 model |
| `parakeet` | `parakeet` | `nvidia/parakeet-tdt-0.6b-v3` | NVIDIA Parakeet NeMo-based ASR, handles long audio via chunking |

### Profile Options

#### Common Options

| Option | Description | Example |
|--------|-------------|---------|
| `Engine` | Transcription engine | `whisperx` (default), `parakeet` |
| `Language` | Language code | `en`, `es`, `fr`, `de`, etc. |

#### WhisperX Options (Engine = "whisperx")

| Option | Description | Example |
|--------|-------------|---------|
| `Model` | Whisper model name | `distil-large-v3.5`, `large-v3`, `medium`, `small` |
| `Device` | Compute device | `cuda`, `cpu` |
| `ComputeType` | Precision | `float16`, `int8`, `float32` |
| `AlignModel` | Alignment model | `WAV2VEC2_ASR_LARGE_LV60K_960H` |
| `VadMethod` | Voice activity detection | `silero`, `pyannote` |
| `Temperature` | Sampling temperature | `0.0`, `0.2`, `0.8` |
| `InitialPrompt` | Prompt to condition transcription | `"Technical vocabulary: API, SDK"` |

#### Parakeet Options (Engine = "parakeet")

| Option | Description | Example |
|--------|-------------|---------|
| `ParakeetModel` | NVIDIA Parakeet model | `nvidia/parakeet-tdt-0.6b-v3` |

**Note:** Parakeet uses Silero VAD to detect speech segments first, then transcribes each segment individually. This ensures quieter speakers are not missed and provides real-time progress tracking via the `progress` field when polling.
