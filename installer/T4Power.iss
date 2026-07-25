; Inno Setup script for T4Power.
;
; Build it with installer\build.ps1 rather than by hand — that script publishes the single-file
; exe first and passes the version and source path in as /D defines, so this file never has to
; hardcode either.
;
; What this installer does beyond copying a file: it hands service registration to the
; application itself (--install-service), because that is where the knowledge lives — the pipe
; ACL, the SCM failure actions, the service description. Duplicating any of it here would give us
; two definitions of "installed" to keep in step.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef SourceExe
  #define SourceExe "..\publish\T4Power.exe"
#endif

#define AppName      "T4Power"
#define AppPublisher "Pierre-Olivier Boulant"
#define AppUrl       "https://github.com/pob31/t4power"
#define ExeName      "T4Power.exe"

[Setup]
AppId={{8F3A6C21-4B7D-4E52-9C18-2D6E5A0B7F94}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE

; The service runs as LocalSystem and writes to Program Files, so this is not optional.
PrivilegesRequired=admin

; Self-contained win-x64 build; there is nothing to install on other architectures.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=Output
OutputBaseFilename=T4Power-{#AppVersion}-setup
SetupIconFile=..\src\T4Power\Assets\t4power.ico
UninstallDisplayIcon={app}\{#ExeName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

; The exe is already a compressed single-file bundle, so let Restart Manager close the tray UI
; rather than failing on a locked file — that is the exact failure this replaces.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE";   DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";            Filename: "{app}\{#ExeName}"
Name: "{group}\Uninstall {#AppName}";  Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";      Filename: "{app}\{#ExeName}"; Tasks: desktopicon

; A Startup shortcut rather than an HKCU Run value, deliberately. This installer is elevated, so
; HKCU is the *elevating* account's hive — which is the signed-in user only when they happened to
; be an administrator. {autostartup} resolves to the common Startup folder in an administrative
; install and is therefore unambiguous.
Name: "{autostartup}\{#AppName}";      Filename: "{app}\{#ExeName}"; Tasks: startuptray

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "startuptray"; Description: "Start the tray UI at sign-in"
Name: "launchtray";  Description: "Start the tray UI now"

[Run]
; Registers and starts the LocalSystem service. Already elevated here, so this raises no second
; UAC prompt.
Filename: "{app}\{#ExeName}"; Parameters: "--install-service"; \
    StatusMsg: "Registering the T4Power service..."; Flags: runhidden waituntilterminated

; Deliberately runasoriginaluser: the tray UI is designed to run unelevated and talk to the
; service over the pipe. Launching it with the installer's admin token would leave it elevated
; for the rest of the session, which is exactly the state that makes later upgrades fail on a
; locked binary.
Filename: "{app}\{#ExeName}"; Description: "Start {#AppName}"; \
    Flags: nowait postinstall skipifsilent runasoriginaluser; Tasks: launchtray

[UninstallRun]
; Runs before the files are removed. Stopping the service restores the GPU's default power limit,
; releases clock locks, and hands any adopted fan header back to the BIOS.
Filename: "{app}\{#ExeName}"; Parameters: "--uninstall-service"; \
    RunOnceId: "RemoveService"; Flags: runhidden waituntilterminated

[Code]
const
  PawnIoUrl = 'https://github.com/namazso/PawnIO.Setup/releases';

var
  PawnIoPage: TOutputMsgMemoWizardPage;

{ PawnIO is a kernel driver, so it registers under Services rather than as an installed program. }
function PawnIoInstalled(): Boolean;
begin
  Result := RegKeyExists(HKEY_LOCAL_MACHINE, 'SYSTEM\CurrentControlSet\Services\PawnIO');
end;

function T4PowerRunning(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('sc.exe', 'query T4Power', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
            and (ResultCode = 0);
end;

{ Stop the service and close the tray before any file is replaced.

  Restart Manager (CloseApplications) handles the tray window, but it will not stop a Windows
  service — and the service holds its own binary open. Without this, an upgrade fails with
  "access denied" partway through, leaving the service stopped and the old exe in place. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    if T4PowerRunning() then
    begin
      Exec('sc.exe', 'stop T4Power', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(2000);
    end;

    { The tray may be running elevated if someone set a "run as administrator" compatibility
      flag on it, in which case Restart Manager cannot close it either. }
    Exec('taskkill.exe', '/IM T4Power.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(500);
  end;
end;

procedure InitializeWizard();
begin
  PawnIoPage := CreateOutputMsgMemoPage(wpSelectTasks,
    'Motherboard fan control',
    'Optional: driving the GPU cooler''s fan header needs one more component.',
    'T4Power can drive the fan header that cools the GPU, using the card''s own temperature. ' +
    'That means writing to the motherboard''s sensor chip, which requires the PawnIO driver.' + #13#10 + #13#10 +
    'PawnIO is free and open source, and is signed by Microsoft''s Hardware Compatibility ' +
    'Publisher so it loads on machines with Memory Integrity (HVCI) enabled — where the older ' +
    'WinRing0 driver cannot.' + #13#10 + #13#10 +
    'Everything else works without it: GPU power limits, clock pinning, profiles and rules are ' +
    'all unaffected. You can install PawnIO later and restart the T4Power service.',
    PawnIoUrl + #13#10 + #13#10 +
    'Setup will offer to open this page when it finishes.');
end;

{ Only show the PawnIO page when the driver is actually missing. }
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = PawnIoPage.ID) and PawnIoInstalled();
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and not PawnIoInstalled() then
    WizardForm.FinishedLabel.Caption :=
      WizardForm.FinishedLabel.Caption + #13#10 + #13#10 +
      'Note: motherboard fan control is unavailable until the PawnIO driver is installed. ' +
      'See ' + PawnIoUrl;
end;

{ Offer the download page at the end rather than during install, so it never steals focus from
  the wizard. Never downloads or runs anything itself — fetching and executing a third-party
  driver installer on the user's behalf is not this program's decision to make. }
procedure DeinitializeSetup();
var
  ResultCode: Integer;
begin
  if PawnIoInstalled() then
    Exit;

  if MsgBox('Open the PawnIO download page now?' + #13#10 + #13#10 +
            'It is needed only for motherboard fan control.',
            mbConfirmation, MB_YESNO) = IDYES then
    ShellExec('open', PawnIoUrl, '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;
