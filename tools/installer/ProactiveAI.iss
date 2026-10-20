; Inno Setup 6 script for the Meeting Copilot installer (setup.exe).
; Built by tools\Publish.ps1, which passes the published folder and version:
;   ISCC.exe /DSourceDir=<published folder> /DAppVersion=<x.y.z> /O<output dir> tools\installer\ProactiveAI.iss
; The installed app is the same self-contained, unpackaged build as the zip.
; The app was called "Proactive AI" up to 0.1.0 (ProactiveAI.exe); the AppId is unchanged, and an installed
; Proactive AI is removed (keeping user data) before Meeting Copilot is installed.

#ifndef SourceDir
  #error Pass /DSourceDir=<published folder> (run tools\Publish.ps1)
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
; Where the app keeps its data and the Credential Manager entry for the API key (see AppDataLocation and
; WindowsCredentialStore), now and under the former name. Overridable only so the uninstall clean-up can be tested
; without touching real data.
#ifndef UserDataDir
  #define UserDataDir "{localappdata}\MeetingCopilot"
#endif
#ifndef LegacyUserDataDir
  #define LegacyUserDataDir "{localappdata}\ProactiveAI"
#endif
#ifndef CredentialTarget
  #define CredentialTarget "MeetingCopilot/OpenAI-API-Key"
#endif
#ifndef LegacyCredentialTarget
  #define LegacyCredentialTarget "ProactiveAI/OpenAI-API-Key"
#endif
; Answer used for the "delete my data" question in silent uninstalls (/SUPPRESSMSGBOXES): keep the data.
#ifndef DeleteDataDefault
  #define DeleteDataDefault "IDNO"
#endif

[Setup]
AppId={{EF6F4600-8A2F-4C4A-8B0C-E0F8C9DBB55B}
AppName=Meeting Copilot
AppVersion={#AppVersion}
AppVerName=Meeting Copilot {#AppVersion}
AppPublisher=Meeting Copilot
AppPublisherURL=https://github.com/yszhang111/meeting_copilot
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Meeting Copilot
VersionInfoCompany=Meeting Copilot
; Per-user install by default (no administrator prompt); the user may choose an all-users install instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\Meeting Copilot
DisableProgramGroupPage=yes
; Windows 10 2004 (build 19041) or later, x64. The Windows echo-cancellation reference control is used only on
; Windows 11 22621+; the default built-in echo canceller works on both.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; The app holds this mutex while running (same name as in Proactive AI); setup and uninstall ask the user to close it.
AppMutex=ProactiveAI.Running
SetupIconFile=..\..\src\ProactiveAI.Desktop\Assets\AppIcon.ico
UninstallDisplayIcon={app}\MeetingCopilot.exe
UninstallDisplayName=Meeting Copilot
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputBaseFilename=MeetingCopilot-{#AppVersion}-win-x64-setup

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Meeting Copilot"; Filename: "{app}\MeetingCopilot.exe"
Name: "{autodesktop}\Meeting Copilot"; Filename: "{app}\MeetingCopilot.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MeetingCopilot.exe"; Description: "{cm:LaunchProgram,Meeting Copilot}"; Flags: nowait postinstall skipifsilent

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{EF6F4600-8A2F-4C4A-8B0C-E0F8C9DBB55B}_is1';
  LegacyExe = 'ProactiveAI.exe';

{ An installed copy of the app under its former name (Proactive AI, same AppId) in the current install mode. }
function FindLegacyInstall(var AppDir, Uninstaller: String): Boolean;
var
  RootKey: Integer;
begin
  if IsAdminInstallMode then
    RootKey := HKLM
  else
    RootKey := HKCU;
  Result := RegQueryStringValue(RootKey, UninstallKey, 'Inno Setup: App Path', AppDir) and
    FileExists(AddBackslash(AppDir) + LegacyExe) and
    RegQueryStringValue(RootKey, UninstallKey, 'UninstallString', Uninstaller);
  if Result then
    Uninstaller := RemoveQuotes(Uninstaller);
end;

procedure InitializeWizard;
var
  AppDir, Uninstaller: String;
begin
  { Upgrading from Proactive AI: install into "Meeting Copilot" rather than the old "Proactive AI" folder. }
  if FindLegacyInstall(AppDir, Uninstaller) and
     (CompareText(RemoveBackslashUnlessRoot(WizardForm.DirEdit.Text), RemoveBackslashUnlessRoot(AppDir)) = 0) then
    WizardForm.DirEdit.Text := ExpandConstant('{autopf}\Meeting Copilot');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  AppDir, Uninstaller: String;
  ResultCode: Integer;
begin
  Result := '';
  if not FindLegacyInstall(AppDir, Uninstaller) then
    exit;
  { Its uninstaller keeps the user's data when run silently; the app moves that data on first start. }
  Log('Removing the previous version (Proactive AI) from ' + AppDir);
  if not Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or
     (ResultCode <> 0) then
    Result := 'The previous version (Proactive AI) could not be removed automatically. ' +
      'Uninstall it from Settings > Apps > Installed apps, then run this setup again.';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep <> usPostUninstall then
    exit;
  if SuppressibleMsgBox('Also delete your Meeting Copilot data on this PC?' + #13#10#13#10 +
      'This removes saved sessions (transcripts and answers), saved resumes, settings, logs and the OpenAI API key ' +
      'stored in Windows Credential Manager.' + #13#10#13#10 +
      'Choose No to keep them for a later reinstall.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, {#DeleteDataDefault}) <> IDYES then
    exit;
  DelTree(ExpandConstant('{#UserDataDir}'), True, True, True);
  DelTree(ExpandConstant('{#LegacyUserDataDir}'), True, True, True);
  Exec(ExpandConstant('{sys}\cmdkey.exe'), '/delete:{#CredentialTarget}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\cmdkey.exe'), '/delete:{#LegacyCredentialTarget}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;