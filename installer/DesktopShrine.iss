#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef PublishRoot
  #error PublishRoot must be supplied by Build-WindowsInstaller.ps1
#endif
#ifndef RepositoryRoot
  #error RepositoryRoot must be supplied by Build-WindowsInstaller.ps1
#endif
#ifndef InstallerOutput
  #error InstallerOutput must be supplied by Build-WindowsInstaller.ps1
#endif

#define MyAppName "Desktop Shrine"
#define MyAppExeName "DesktopShrine.Host.exe"
#define StartupTaskName "Desktop Shrine"
#define GOverlayMsiUri "https://www.goverlay.com/downloads/lcdsysinfo/GOverlaySetup.msi"
#define GOverlayMsiSha256 "7F0B3EBF8422D4D68402B3789944EC8CB9D401E3756751FEEB2AC3AB48F3B5E4"

[Setup]
AppId={{17BCE957-E0DB-4725-A31B-A18F8D9DD7FC}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Desktop Shrine
DefaultDirName={autopf}\Desktop Shrine
DefaultGroupName=Desktop Shrine
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#InstallerOutput}
OutputBaseFilename=DesktopShrine-Setup-{#MyAppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
UsedUserAreasWarning=no
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription=Desktop Shrine installer
VersionInfoProductName=Desktop Shrine
VersionInfoProductVersion={#MyAppVersion}

[Tasks]
Name: "startup"; Description: "Start Desktop Shrine when I sign in to Windows"; GroupDescription: "Startup options:"; Flags: checkedonce
Name: "runasadmin"; Description: "Run Desktop Shrine as administrator (recommended - hardware status can be incomplete in user mode)"; GroupDescription: "Startup options:"; Flags: checkedonce
Name: "goverlay"; Description: "Download and install legacy GOverlay 1.6.9 from the official website"; GroupDescription: "Optional hardware software:"; Flags: unchecked

[Files]
Source: "{#PublishRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepositoryRoot}\LICENSE"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "{#RepositoryRoot}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "{#RepositoryRoot}\third-party-licenses\Oxanium-OFL-1.1.txt"; DestDir: "{app}\licenses\third-party-licenses"; Flags: ignoreversion

[Icons]
Name: "{group}\Desktop Shrine"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--launch"; WorkingDir: "{app}"
Name: "{group}\Desktop Shrine configuration"; Filename: "{localappdata}\Desktop Shrine\configuration"
Name: "{group}\Uninstall Desktop Shrine"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Desktop Shrine"; Flags: deletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Desktop Shrine"; ValueData: """{app}\{#MyAppExeName}"""; Check: UseNormalStartup
Root: HKCU; Subkey: "Software\Desktop Shrine"; ValueType: dword; ValueName: "RunAsAdministrator"; ValueData: "1"; Check: UseAdministratorMode
Root: HKCU; Subkey: "Software\Desktop Shrine"; ValueType: dword; ValueName: "RunAsAdministrator"; ValueData: "0"; Check: UseUserMode

[Run]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#StartupTaskName}"" /F"; Flags: runhidden waituntilterminated; StatusMsg: "Updating Desktop Shrine startup..."
Filename: "{sys}\schtasks.exe"; Parameters: "/Create /TN ""{#StartupTaskName}"" /SC ONLOGON /RL HIGHEST /TR """"{app}\{#MyAppExeName}"""" /F"; Flags: runhidden waituntilterminated; Tasks: startup and runasadmin; StatusMsg: "Registering elevated startup..."
Filename: "{sys}\msiexec.exe"; Parameters: "/i ""{tmp}\GOverlaySetup.msi"""; Flags: waituntilterminated; Tasks: goverlay; StatusMsg: "Installing legacy GOverlay..."
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\integrations\goverlay\Deploy-GOverlayPlugin.ps1"" -PluginAssemblyPath ""{app}\integrations\goverlay\DesktopShrine.Plugin.GOverlay.LegacySdk.Release.dll"" -InstallDirectory ""{code:GetGOverlayDirectory}"" -ProcessName GOverlay -ElevatedDeployment"; Flags: runhidden waituntilterminated; Check: CanDeployGOverlayBridge; StatusMsg: "Installing the Desktop Shrine GOverlay bridge..."
Filename: "{app}\{#MyAppExeName}"; Parameters: "--launch"; Description: "Start Desktop Shrine now"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#MyAppExeName} /F"; Flags: runhidden waituntilterminated; RunOnceId: "StopDesktopShrine"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#StartupTaskName}"" /F"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveDesktopShrineStartupTask"

[UninstallDelete]
Type: files; Name: "{code:GetGOverlayDirectory}\Plugins\DesktopShrine.Plugin.GOverlay.LegacySdk.dll"
Type: files; Name: "{code:GetGOverlayDirectory}\Plugins\DesktopShrine.Plugin.GOverlay.LegacySdk.Debug.dll"
Type: files; Name: "{code:GetGOverlayDirectory}\Plugins\DesktopShrine.Plugin.GOverlay.LegacySdk.Release.dll"
Type: files; Name: "{code:GetGOverlayDirectory}\Plugins\DesktopShrine.Plugin.GOverlay.Layout.dll"

[Code]
function IsTaskSelected(const Description: String): Boolean;
begin
  Result := WizardIsTaskSelected(Description);
end;

function UseAdministratorMode: Boolean;
begin
  Result := IsTaskSelected('runasadmin');
end;

function UseUserMode: Boolean;
begin
  Result := not UseAdministratorMode();
end;

function UseNormalStartup: Boolean;
begin
  Result := IsTaskSelected('startup') and UseUserMode();
end;

function GetGOverlayDirectory(Param: String): String;
begin
  Result := ExpandConstant('{pf32}\GOverlay');
end;

function CanDeployGOverlayBridge: Boolean;
begin
  Result := DirExists(GetGOverlayDirectory('') + '\Plugins') and
    FileExists(GetGOverlayDirectory('') + '\Interfaces.dll');
end;

function OnGOverlayDownloadProgress(
  const Url, FileName: String;
  const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  RunKey: String;
begin
  if CurStep = ssPostInstall then
  begin
    if not IsTaskSelected('startup') then
    begin
      RunKey := 'Software\Microsoft\Windows\CurrentVersion\Run';
      RegDeleteValue(HKCU, RunKey, 'Desktop Shrine');
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if IsTaskSelected('goverlay') then
  begin
    try
      Log('Downloading the official legacy GOverlay installer...');
      DownloadTemporaryFile(
        '{#GOverlayMsiUri}',
        'GOverlaySetup.msi',
        '{#GOverlayMsiSha256}',
        @OnGOverlayDownloadProgress);
    except
      Result :=
        'The official legacy GOverlay installer could not be downloaded or ' +
        'verified.' + #13#10 + GetExceptionMessage;
      exit;
    end;
  end;

  Exec(
    ExpandConstant('{sys}\taskkill.exe'),
    '/IM {#MyAppExeName} /F',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RegDeleteValue(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'Desktop Shrine');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Desktop Shrine');
  end;
end;
