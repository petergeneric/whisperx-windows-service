# WhisperX API Service - Build & Installer Makefile
# Requires: .NET 8 SDK, NSIS 3.x (makensis)

PROJECT = WhisperXApi
PUBLISH_DIR = bin/publish
INSTALLER_DIR = bin/installer
VERSION = 1.0.0

# .NET publish settings
RUNTIME = win-x64
CONFIG = Release

# UV version to bundle (0.9.x+ required for --torch-backend support)
UV_VERSION = 0.9.26
UV_URL = https://github.com/astral-sh/uv/releases/download/$(UV_VERSION)/uv-x86_64-pc-windows-msvc.zip

# FFmpeg version to bundle (required for audio decoding)
FFMPEG_VERSION = 7.1
FFMPEG_URL = https://github.com/GyanD/codexffmpeg/releases/download/$(FFMPEG_VERSION)/ffmpeg-$(FFMPEG_VERSION)-essentials_build.zip

# NSIS compiler (install via: brew install nsis, choco install nsis, or apt install nsis)
MAKENSIS = makensis

.PHONY: all build publish installer clean help download-uv download-ffmpeg download-deps

all: installer

help:
	@echo "WhisperX API Service - Build Targets"
	@echo ""
	@echo "  make build         - Build the project (release)"
	@echo "  make publish       - Publish single-file Windows executable"
	@echo "  make installer     - Create NSIS installer (.exe)"
	@echo "  make clean         - Remove build artifacts"
	@echo ""
	@echo "Requirements:"
	@echo "  - .NET 8 SDK"
	@echo "  - NSIS 3.x"
	@echo ""

build:
	dotnet build -c $(CONFIG)

publish: clean
	@mkdir -p $(INSTALLER_DIR)
	dotnet publish -c $(CONFIG) \
		-r $(RUNTIME) \
		--self-contained true \
		-p:PublishSingleFile=true \
		-p:IncludeNativeLibrariesForSelfExtract=true \
		-p:EnableCompressionInSingleFile=true \
		-o $(PUBLISH_DIR)

download-uv:
	@echo "Downloading uv $(UV_VERSION)..."
	@mkdir -p $(PUBLISH_DIR)
	@curl -L -o $(PUBLISH_DIR)/uv.zip $(UV_URL)
	@unzip -o $(PUBLISH_DIR)/uv.zip -d $(PUBLISH_DIR)
	@rm $(PUBLISH_DIR)/uv.zip
	@echo "uv binaries downloaded to $(PUBLISH_DIR)/"

download-ffmpeg:
	@echo "Downloading ffmpeg $(FFMPEG_VERSION)..."
	@mkdir -p $(PUBLISH_DIR)
	@curl -L -o $(PUBLISH_DIR)/ffmpeg.zip $(FFMPEG_URL)
	@unzip -o $(PUBLISH_DIR)/ffmpeg.zip -d $(PUBLISH_DIR)
	@cp $(PUBLISH_DIR)/ffmpeg-$(FFMPEG_VERSION)-essentials_build/bin/ffmpeg.exe $(PUBLISH_DIR)/
	@rm -rf $(PUBLISH_DIR)/ffmpeg-$(FFMPEG_VERSION)-essentials_build $(PUBLISH_DIR)/ffmpeg.zip
	@echo "ffmpeg downloaded to $(PUBLISH_DIR)/"

download-deps: download-uv download-ffmpeg

installer: publish download-deps
	@echo "Building NSIS installer..."
	@cd installer && $(MAKENSIS) \
		-DVERSION=$(VERSION) \
		-DOUTFILE=../$(INSTALLER_DIR)/WhisperXApi-Setup-$(VERSION).exe \
		whisperx-api.nsi
	@echo ""
	@echo "Installer created: $(INSTALLER_DIR)/WhisperXApi-Setup-$(VERSION).exe"
	@echo ""

clean:
	rm -rf bin/
	rm -rf obj/
