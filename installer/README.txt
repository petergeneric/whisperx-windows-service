WhisperX API Service
====================

A REST API service for WhisperX audio transcription with CUDA support.

PREREQUISITES
-------------
- Windows 10/11 or Windows Server 2016+
- NVIDIA GPU with CUDA support
- CUDA Toolkit 12.8+

Note: UV is bundled with the installer - no need to install separately.

FIREWALL
--------
The installer automatically adds a Windows Firewall rule.
The service listens on all network interfaces (0.0.0.0).

Access locally:      http://localhost:5173
Access from network: http://<machine-ip>:5173

USAGE
-----
Create a transcription job:
  curl -X POST http://localhost:5173/jobs -F "file=@audio.wav"
  Returns: {"id":"<job-id>"}

Check job status:
  curl http://localhost:5173/jobs/<job-id>
  Returns: {"id":"...","status":"queued|processing|completed|failed",...}

Delete a job:
  curl -X DELETE http://localhost:5173/jobs/<job-id>

CONFIGURATION
-------------
Edit: C:\Program Files\WhisperXApi\appsettings.json

Key settings:
- Urls: API endpoint (e.g., "http://0.0.0.0:5173")
- WhisperX.TempDirectory: Temporary file storage
- WhisperX.Profiles: Transcription model profiles

Restart service after changes:
  net stop WhisperXApi
  net start WhisperXApi

SERVICE MANAGEMENT
------------------
Start:   net start WhisperXApi
Stop:    net stop WhisperXApi
Status:  sc query WhisperXApi

TROUBLESHOOTING
---------------
View logs in Event Viewer:
  Windows Logs > Application > Source: WhisperXApi
