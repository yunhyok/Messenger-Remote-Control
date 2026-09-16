#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef ProjectRoot
  #error ProjectRoot is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif

#ifdef MasterBuild
  #ifndef DotNetInstaller
    #error DotNetInstaller is required for the Master installer
  #endif
  #define AppRole "Master"
  #define ProductName "Messenger Remote Control Master"
  #define ExecutableName "RemoteMonitorMaster.exe"
  #define SourceDirectory ProjectRoot + "\src\RemoteMonitorMaster\bin\Release\net48"
  #define SetupName "Messenger-Remote-Control-Master-Setup-" + AppVersion
  #define AppIdentifier "{{3F6487C1-1D80-4C38-8CAB-92C22E3DF4A0}"
  #define AppMutexName "Local\RemoteMonitorMaster"
  #define RequiredWindows "6.1sp1"
#else
  #define AppRole "Slave"
  #define ProductName "Messenger Remote Control Slave"
  #define ExecutableName "RemoteMonitorSlave.exe"
  #define SourceDirectory ProjectRoot + "\src\RemoteMonitorSlave\bin\Release\net48"
  #define SetupName "Messenger-Remote-Control-Slave-Setup-" + AppVersion
  #define AppIdentifier "{{8D85E8BA-6F8E-4B51-BA88-4B8B6D515627}"
  #define AppMutexName "Local\RemoteMonitorSlave"
  #define RequiredWindows "10.0.22000"
#endif

[Setup]
AppId={#AppIdentifier}
AppName={#ProductName}
AppVersion={#AppVersion}
AppVerName={#ProductName} v{#AppVersion}
AppPublisher=yunhyok
DefaultDirName={localappdata}\Programs\Messenger Remote Control\{#AppRole}
DefaultGroupName=Messenger Remote Control
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x86compatible
MinVersion={#RequiredWindows}
OutputDir={#OutputDir}
OutputBaseFilename={#SetupName}
Compression=lzma2/max
SolidCompression=yes
SetupLogging=yes
CloseApplications=yes
CloseApplicationsFilter={#ExecutableName}
RestartApplications=no
AppMutex={#AppMutexName}
UninstallDisplayName={#ProductName} v{#AppVersion}
UninstallDisplayIcon={app}\{#ExecutableName}
VersionInfoCompany=Messenger Remote Control
VersionInfoDescription={#ProductName} Setup
VersionInfoProductName={#ProductName}
VersionInfoProductVersion={#AppVersion}
VersionInfoVersion={#AppVersion}.0

[Files]
Source: "{#SourceDirectory}\{#ExecutableName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDirectory}\{#ExecutableName}.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ProjectRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ProjectRoot}\INSTALL.md"; DestDir: "{app}"; Flags: ignoreversion
#ifdef MasterBuild
Source: "{#DotNetInstaller}"; Flags: dontcopy
#endif

[Icons]
Name: "{autoprograms}\Messenger Remote Control\{#ProductName}"; Filename: "{app}\{#ExecutableName}"; WorkingDir: "{app}"

[Code]
const
  NetFramework48Release = 528040;
  DotNetInstallerName = 'NDP48-x86-x64-AllOS-ENU.exe';

function HasNetFramework48: Boolean;
var
  Release: Cardinal;
begin
  Result :=
    (RegQueryDWordValue(HKLM32,
      'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full',
      'Release', Release) and (Release >= NetFramework48Release));
  if (not Result) and IsWin64 then
    Result :=
      (RegQueryDWordValue(HKLM64,
        'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full',
        'Release', Release) and (Release >= NetFramework48Release));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
#ifdef MasterBuild
var
  ResultCode: Integer;
  InstallerPath: String;
#endif
begin
  Result := '';
  if HasNetFramework48 then
    exit;

#ifdef MasterBuild
  ExtractTemporaryFile(DotNetInstallerName);
  InstallerPath := ExpandConstant('{tmp}\' + DotNetInstallerName);
  Log('Installing the bundled Microsoft .NET Framework 4.8 prerequisite.');
  if not ShellExec('runas', InstallerPath,
    '/passive /showrmui /norestart /ChainingPackage MessengerRemoteControlMaster',
    '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode = 1223 then
      Result := 'Microsoft .NET Framework 4.8 elevation was canceled. The application was not installed.'
    else
      Result := Format('Microsoft .NET Framework 4.8 could not be started (Windows error %d: %s). The application was not installed.', [ResultCode, SysErrorMessage(ResultCode)]);
    exit;
  end;

  Log(Format('Microsoft .NET Framework 4.8 installer exit code: %d', [ResultCode]));
  if (ResultCode = 1641) or (ResultCode = 3010) then
  begin
    NeedsRestart := True;
    Result := 'Microsoft .NET Framework 4.8 was installed and Windows must restart before Messenger Remote Control Master can be installed. Restart Windows, then run this setup again.';
    exit;
  end;
  if ResultCode = 1602 then
  begin
    Result := 'Microsoft .NET Framework 4.8 installation was canceled. The application was not installed.';
    exit;
  end;
  if ResultCode <> 0 then
  begin
    Result := Format('Microsoft .NET Framework 4.8 installation failed with exit code %d. The application was not installed.', [ResultCode]);
    exit;
  end;
  if not HasNetFramework48 then
    Result := 'Microsoft .NET Framework 4.8 setup completed without registering the required runtime. The application was not installed.';
#else
  Result := 'Messenger Remote Control Slave requires Microsoft .NET Framework 4.8 or later. Install the runtime, then run this setup again.';
#endif
end;
