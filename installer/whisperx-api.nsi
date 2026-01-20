; WhisperX API Service - NSIS Installer Script
; Requires: NSIS 3.x

;--------------------------------
; Includes

!include "MUI2.nsh"
!include "nsDialogs.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

;--------------------------------
; General

!define PRODUCT_NAME "WhisperX API"
!define PRODUCT_VERSION "1.0.0"
!define PRODUCT_PUBLISHER "WhisperX"
!define PRODUCT_WEB_SITE "https://github.com/m-bain/whisperX"
!define PRODUCT_DIR_REGKEY "Software\Microsoft\Windows\CurrentVersion\App Paths\WhisperXApi.exe"
!define PRODUCT_UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}"
!define PRODUCT_UNINST_ROOT_KEY "HKLM"

!define SERVICE_NAME "WhisperXApi"
!define SERVICE_DISPLAY_NAME "WhisperX Transcription API"
!define SERVICE_DESCRIPTION "REST API service for WhisperX audio transcription with CUDA support"

; Installer attributes
Name "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "..\bin\installer\WhisperXApi-Setup-${PRODUCT_VERSION}.exe"
InstallDir "$PROGRAMFILES64\WhisperXApi"
InstallDirRegKey HKLM "${PRODUCT_DIR_REGKEY}" ""
RequestExecutionLevel admin
ShowInstDetails show
ShowUnInstDetails show

;--------------------------------
; Variables

Var PortNumber
Var PortNumberText

;--------------------------------
; Interface Settings

!define MUI_ABORTWARNING
!define MUI_ICON "${NSISDIR}\Contrib\Graphics\Icons\modern-install.ico"
!define MUI_UNICON "${NSISDIR}\Contrib\Graphics\Icons\modern-uninstall.ico"

;--------------------------------
; Pages

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "LICENSE.txt"
!insertmacro MUI_PAGE_DIRECTORY
Page custom PortConfigPage PortConfigPageLeave
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

;--------------------------------
; Languages

!insertmacro MUI_LANGUAGE "English"

;--------------------------------
; Custom Page - Port Configuration

Function PortConfigPage
    nsDialogs::Create 1018
    Pop $0

    ${NSD_CreateLabel} 0 0 100% 12u "Configure the API port number:"
    Pop $0

    ${NSD_CreateLabel} 0 20u 60u 12u "Port:"
    Pop $0

    ${NSD_CreateText} 65u 18u 50u 12u "5173"
    Pop $PortNumberText

    ${NSD_CreateLabel} 0 40u 100% 24u "The API will listen on all network interfaces (0.0.0.0:<port>).$\nDefault port is 5173."
    Pop $0

    nsDialogs::Show
FunctionEnd

Function PortConfigPageLeave
    ${NSD_GetText} $PortNumberText $PortNumber

    ; Validate port number
    ${If} $PortNumber == ""
        StrCpy $PortNumber "5173"
    ${EndIf}
FunctionEnd

;--------------------------------
; Installer Sections

Section "Main Application" SecMain
    SectionIn RO

    SetOutPath "$INSTDIR"
    SetOverwrite on

    ; Stop existing service if running
    DetailPrint "Checking for existing service..."
    nsExec::ExecToLog 'sc query ${SERVICE_NAME}'
    Pop $0
    ${If} $0 == 0
        DetailPrint "Stopping existing service..."
        nsExec::ExecToLog 'sc stop ${SERVICE_NAME}'
        Sleep 2000
        DetailPrint "Removing existing service..."
        nsExec::ExecToLog 'sc delete ${SERVICE_NAME}'
        Sleep 1000
    ${EndIf}

    ; Install files
    DetailPrint "Installing files..."
    File "..\bin\publish\WhisperXApi.exe"
    File "..\bin\publish\uv.exe"
    File "..\bin\publish\uvx.exe"
    File "..\bin\publish\ffmpeg.exe"
    File "..\appsettings.json"
    File "README.txt"

    ; Update config with port number and local uvx path
    DetailPrint "Configuring service..."
    nsExec::ExecToLog 'powershell -ExecutionPolicy Bypass -Command "\
        $$config = Get-Content \"$INSTDIR\appsettings.json\" -Raw | ConvertFrom-Json; \
        $$config.Urls = \"http://0.0.0.0:$PortNumber\"; \
        $$config.WhisperX.UvxPath = \"$INSTDIR\uvx.exe\"; \
        $$config | ConvertTo-Json -Depth 10 | Set-Content \"$INSTDIR\appsettings.json\""'

    ; Create temp and cache directories
    CreateDirectory "C:\temp\whisperx-api"
    CreateDirectory "C:\temp\whisperx-api\cache"

    ; Create the Windows Service (runs as Local System)
    DetailPrint "Creating Windows Service..."
    nsExec::ExecToLog 'sc create ${SERVICE_NAME} binPath= "\"$INSTDIR\WhisperXApi.exe\"" start= auto DisplayName= "${SERVICE_DISPLAY_NAME}"'
    Pop $0
    ${If} $0 != 0
        MessageBox MB_OK|MB_ICONEXCLAMATION "Failed to create service. You may need to create it manually."
    ${EndIf}

    ; Set service description
    nsExec::ExecToLog 'sc description ${SERVICE_NAME} "${SERVICE_DESCRIPTION}"'

    ; Configure service recovery (restart on failure)
    nsExec::ExecToLog 'sc failure ${SERVICE_NAME} reset= 86400 actions= restart/5000/restart/10000/restart/30000'

    ; Add firewall rule for network access
    DetailPrint "Adding firewall rule for port $PortNumber..."
    nsExec::ExecToLog 'netsh advfirewall firewall delete rule name="WhisperX API"'
    nsExec::ExecToLog 'netsh advfirewall firewall add rule name="WhisperX API" dir=in action=allow protocol=tcp localport=$PortNumber'

    ; Start the service
    DetailPrint "Starting service..."
    nsExec::ExecToLog 'sc start ${SERVICE_NAME}'
    Pop $0
    ${If} $0 != 0
        MessageBox MB_OK|MB_ICONINFORMATION "Service installed but failed to start. Check Event Viewer for details."
    ${EndIf}

    ; Create uninstaller
    WriteUninstaller "$INSTDIR\uninstall.exe"

    ; Registry entries
    WriteRegStr HKLM "${PRODUCT_DIR_REGKEY}" "" "$INSTDIR\WhisperXApi.exe"
    WriteRegStr ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "DisplayName" "$(^Name)"
    WriteRegStr ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "UninstallString" "$INSTDIR\uninstall.exe"
    WriteRegStr ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "DisplayIcon" "$INSTDIR\WhisperXApi.exe"
    WriteRegStr ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
    WriteRegStr ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "URLInfoAbout" "${PRODUCT_WEB_SITE}"
    WriteRegStr ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
    WriteRegDWORD ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "NoModify" 1
    WriteRegDWORD ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "NoRepair" 1

    ; Get installed size
    ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
    IntFmt $0 "0x%08X" $0
    WriteRegDWORD ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "EstimatedSize" "$0"

    ; Create Start Menu shortcuts
    CreateDirectory "$SMPROGRAMS\${PRODUCT_NAME}"
    CreateShortCut "$SMPROGRAMS\${PRODUCT_NAME}\Uninstall.lnk" "$INSTDIR\uninstall.exe"
    CreateShortCut "$SMPROGRAMS\${PRODUCT_NAME}\README.lnk" "$INSTDIR\README.txt"

SectionEnd

;--------------------------------
; Uninstaller Section

Section "Uninstall"

    ; Stop and remove service
    DetailPrint "Stopping service..."
    nsExec::ExecToLog 'sc stop ${SERVICE_NAME}'
    Sleep 2000

    DetailPrint "Removing service..."
    nsExec::ExecToLog 'sc delete ${SERVICE_NAME}'
    Sleep 1000

    ; Remove files
    DetailPrint "Removing files..."
    Delete "$INSTDIR\WhisperXApi.exe"
    Delete "$INSTDIR\uv.exe"
    Delete "$INSTDIR\uvx.exe"
    Delete "$INSTDIR\ffmpeg.exe"
    Delete "$INSTDIR\appsettings.json"
    Delete "$INSTDIR\README.txt"
    Delete "$INSTDIR\uninstall.exe"
    RMDir "$INSTDIR"

    ; Remove temp and cache directories (ask user first)
    MessageBox MB_YESNO|MB_ICONQUESTION "Remove temporary files and model cache in C:\temp\whisperx-api?$\n$\nThis includes downloaded AI models which may be several GB." IDNO SkipTempRemoval
        RMDir /r "C:\temp\whisperx-api"
    SkipTempRemoval:

    ; Remove Start Menu shortcuts
    Delete "$SMPROGRAMS\${PRODUCT_NAME}\Uninstall.lnk"
    Delete "$SMPROGRAMS\${PRODUCT_NAME}\README.lnk"
    RMDir "$SMPROGRAMS\${PRODUCT_NAME}"

    ; Remove firewall rule
    DetailPrint "Removing firewall rule..."
    nsExec::ExecToLog 'netsh advfirewall firewall delete rule name="WhisperX API"'

    ; Remove registry entries
    DeleteRegKey ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}"
    DeleteRegKey HKLM "${PRODUCT_DIR_REGKEY}"

    DetailPrint "Uninstallation complete."

SectionEnd

;--------------------------------
; Section Descriptions

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
    !insertmacro MUI_DESCRIPTION_TEXT ${SecMain} "WhisperX API Service and required files."
!insertmacro MUI_FUNCTION_DESCRIPTION_END
