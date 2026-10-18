; Inno Setup 6 script for the Proactive AI installer (setup.exe).
; Built by tools\Publish.ps1, which passes the published folder and version:
;   ISCC.exe /DSourceDir=<published folder> /DAppVersion=<x.y.z> /O<output dir> tools\installer\ProactiveAI.iss
; The installed app is the same self-contained, unpackaged build as the zip.

#ifndef SourceDir
  #error Pass /DSourceDir=<published folder> (run tools\Publish.ps1)
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
; Where the app keeps its data and the Credential Manager entry for the API key (see JsonSettingsStore and
; WindowsCredentialStore). Overridable only so the uninstall clean-up can be tested without touching real data.
#ifndef UserDataDir
  #define UserDataDir "{localappdata}\ProactiveAI"
#endif
#ifndef CredentialTarget
  #define CredentialTarget "ProactiveAI/OpenAI-API-Key"
#endif
; Answer used for the "delete my data" question in silent uninstalls (/SUPPRESSMSGBOXES): keep the data.
#ifndef DeleteDataDefault
  #define DeleteDataDefault "IDNO"
#endif

[Setup]
AppId={{EF6F4600-8A2F-4C4A-8B0C-E0F8C9DBB55B}
AppName=Proactive AI
AppVersion={#AppVersion}
AppVerName=Proactive AI {#AppVersion}
AppPublisher=ProactiveAI
AppPublisherURL=https://github.com/yszhang111/meeting_copilot
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Proactive AI
VersionInfoCompany=ProactiveAI
; Per-user install by default (no administrator prompt); the user may choose an all-users install instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\Proactive AI
DisableProgramGroupPage=yes
; Windows 10 2004 (build 19041) or later, x64. The Windows echo-cancellation reference control is used only on
; Windows 11 22621+; the default built-in echo canceller works on both.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; The app holds this mutex while running; setup and uninstall ask the user to close it first.
AppMutex=ProactiveAI.Running
SetupIconFile=..\..\src\ProactiveAI.Desktop\Assets\AppIcon.ico
UninstallDisplayIcon={app}\ProactiveAI.exe
UninstallDisplayName=Proactive AI
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputBaseFilename=ProactiveAI-{#AppVersion}-win-x64-setup

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Proactive AI"; Filename: "{app}\ProactiveAI.exe"
Name: "{autodesktop}\Proactive AI"; Filename: "{app}\ProactiveAI.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ProactiveAI.exe"; Description: "{cm:LaunchProgram,Proactive AI}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep <> usPostUninstall then
    exit;
  if SuppressibleMsgBox('Also delete your Proactive AI data on this PC?' + #13#10#13#10 +
      'This removes saved sessions (transcripts and answers), saved resumes, settings, logs and the OpenAI API key ' +
      'stored in Windows Credential Manager.' + #13#10#13#10 +
      'Choose No to keep them for a later reinstall.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, {#DeleteDataDefault}) <> IDYES then
    exit;
  DelTree(ExpandConstant('{#UserDataDir}'), True, True, True);
  Exec(ExpandConstant('{sys}\cmdkey.exe'), '/delete:{#CredentialTarget}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
