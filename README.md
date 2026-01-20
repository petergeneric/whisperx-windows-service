# WhisperX Windows REST Service

This project exposes a simple REST API that can be used to run WhisperX remotely on a Windows machine.
It also includes functionality to remotely request shutdown, so that once transcription completes the machine can be powered off.


# Setup

```
make installer


# Deploy bin/installer/WhisperXApi-Setup-1.0.0.exe to your Windows machine
```

At startup, it creates `c:\temp\whisperx-api\api-key.txt` containing API key that should be sent in API calls

