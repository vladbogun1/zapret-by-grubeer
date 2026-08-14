; Inno Setup script for Запрет by Grubeer.
;
; Build the payload first (from the repository root):
;   dotnet publish Zapret.App\Zapret.App.csproj         -c Release -r win-x64 --self-contained true -o installer\payload
;   dotnet publish Zapret.Service\Zapret.Service.csproj -c Release -r win-x64 --self-contained true -o installer\payload
; Both publish into the same folder on purpose: they share the .NET runtime files, so the installer
; carries one copy instead of two.
;
; Then compile:  iscc installer\ZapretByGrubeer.iss

#define AppDisplayName "Запрет by Grubeer"
#define AppAsciiName   "Zapret by Grubeer"

; CI passes the version from the tag as /DAppVersion=x.y.z; this is only the local fallback.
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppPublisher   "Grubeer"
#define AppExeName     "ZapretByGrubeer.exe"
#define ServiceExeName "ZapretByGrubeer.Service.exe"
#define ServiceName    "ZapretByGrubeer"
#define AppUserModelId "Grubeer.ZapretByGrubeer"

[Setup]
; A stable AppId is what lets a later version upgrade this install instead of duplicating it.
AppId={{8E2C7A41-5D3B-4F16-9A62-7C1D4B8E9F03}
AppName={#AppDisplayName}
AppVersion={#AppVersion}
AppVerName={#AppDisplayName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppDisplayName}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppDisplayName} Setup

; The engine is never installed here; only the manager binaries are. The install directory is the
; user's choice and may be on any drive, so nothing in the product assumes drive C: or Program Files.
DefaultDirName={autopf}\{#AppAsciiName}
DisableDirPage=no
AllowUNCPath=no
DefaultGroupName={#AppDisplayName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppDisplayName}
UninstallDisplayIcon={app}\{#AppExeName}

PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
OutputDir=Output
OutputBaseFilename=ZapretByGrubeer-{#AppVersion}-setup
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "startmenuicon"; Description: "{cm:CreateStartMenuIcon}"; GroupDescription: "{cm:AdditionalIcons}"
; Unchecked by default, as required.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[CustomMessages]
en.CreateStartMenuIcon=Create a Start Menu shortcut
en.CreateDesktopIcon=Create a desktop shortcut
en.AdditionalIcons=Shortcuts:
en.LaunchAfterInstall=Launch {#AppDisplayName}
ru.CreateStartMenuIcon=Создать ярлык в меню «Пуск»
ru.CreateDesktopIcon=Создать ярлык на рабочем столе
ru.AdditionalIcons=Ярлыки:
ru.LaunchAfterInstall=Запустить {#AppDisplayName}

[Files]
Source: "payload\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Exactly one Start Menu entry. Utility actions live inside the application, not as extra shortcuts.
; AppUserModelID is what makes native toast notifications work for an unpackaged application.
Name: "{group}\{#AppDisplayName}"; Filename: "{app}\{#AppExeName}"; AppUserModelID: "{#AppUserModelId}"
Name: "{autodesktop}\{#AppDisplayName}"; Filename: "{app}\{#AppExeName}"; AppUserModelID: "{#AppUserModelId}"; Tasks: desktopicon

[Run]
; Register and start the privileged service. sc.exe receives each argument separately, so no quoting
; gymnastics are needed for a path with spaces.
Filename: "{sys}\sc.exe"; \
  Parameters: "create {#ServiceName} binPath= ""{app}\{#ServiceExeName}"" DisplayName= ""{#AppDisplayName} service"" start= auto"; \
  Flags: runhidden waituntilterminated; StatusMsg: "Registering the background service..."
Filename: "{sys}\sc.exe"; \
  Parameters: "description {#ServiceName} ""Manages the Flowseal Zapret engine for {#AppDisplayName}."""; \
  Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; Flags: runhidden waituntilterminated; \
  StatusMsg: "Starting the background service..."

Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchAfterInstall}"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop the UI and the service before anything is removed.
Filename: "{sys}\taskkill.exe"; Parameters: "/im {#AppExeName} /f"; Flags: runhidden; RunOnceId: "StopUi"
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"

; The privileged binary undoes the privileged changes; the flags come from the questions asked below.
Filename: "{app}\{#ServiceExeName}"; Parameters: "--cleanup {code:GetCleanupFlags}"; \
  Flags: runhidden waituntilterminated; RunOnceId: "Cleanup"; StatusMsg: "Removing engine and settings..."

Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"

[Code]
var
  RemoveEngine: Boolean;
  KeepSettings: Boolean;

{ Uninstall choices, asked before anything is touched. The wording avoids Flowseal internals: the user
  is choosing whether the downloaded engine and their own lists survive. }
function InitializeUninstall(): Boolean;
begin
  Result := True;

  RemoveEngine := MsgBox(
    'Remove the installed Zapret engine as well?' + #13#10#13#10 +
    'Yes — remove {#AppDisplayName} and the engine it downloaded.' + #13#10 +
    'No — remove only {#AppDisplayName} and leave the engine files in place.',
    mbConfirmation, MB_YESNO) = IDYES;

  KeepSettings := MsgBox(
    'Keep your settings and custom lists?' + #13#10#13#10 +
    'Yes — your domain and address lists are preserved for a future install.' + #13#10 +
    'No — remove them together with the application.',
    mbConfirmation, MB_YESNO) = IDYES;
end;

function GetCleanupFlags(Param: String): String;
begin
  Result := '';
  if RemoveEngine then Result := Result + ' --remove-engine';
  if KeepSettings then Result := Result + ' --keep-settings';
end;

{ A Cyrillic install path would break the upstream engine, but the engine never lives in the install
  directory, so the only real risk is a path the .NET host cannot handle. Warn rather than forbid. }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Path: String;
  I: Integer;
begin
  Result := True;
  if CurPageID <> wpSelectDir then Exit;

  Path := WizardDirValue;
  for I := 1 to Length(Path) do
  begin
    if Ord(Path[I]) > 126 then
    begin
      Result := MsgBox(
        'The chosen folder contains non-Latin characters.' + #13#10#13#10 +
        'The application itself handles this, and the network engine is always installed in a separate ' +
        'Latin-only folder. Continue anyway?',
        mbConfirmation, MB_YESNO) = IDYES;
      Exit;
    end;
  end;
end;
