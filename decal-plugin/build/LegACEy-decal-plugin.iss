; Inno Setup 6 script for LegACEy-decal-plugin-v<version>-setup.exe. The release workflow compiles it after packaging:
;   iscc /DPackageDir=<package directory> /DAppVersion=<version> /O<output directory> LegACEy-decal-plugin.iss
; PackageDir is the release zip's layout (New-ReleasePackage.ps1's package\); everything in it is installed.
;
; Setup runs as the player, not elevated, so {localappdata} is the profile of whoever started it even when an admin
; approves the UAC prompt with another account; Inno's user constants follow the account Setup runs as. Only Decal's
; HKLM registration needs admin, so that one step runs elevated reg.exe.

#ifndef PackageDir
  #error Pass /DPackageDir=<package directory>
#endif
#ifndef AppVersion
  #error Pass /DAppVersion=<version>
#endif

[Setup]
AppId={{09A97F72-DB92-4EEB-B49F-776AE829D472}
AppName=LegACEy Decal Plugin
AppVersion={#AppVersion}
AppPublisher=LegACEy
AppPublisherURL=https://github.com/apourman/LegACEy
DefaultDirName={localappdata}\LegACEy\DecalPlugin
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=LegACEy-decal-plugin-v{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[InstallDelete]
; Setup replaces these folders wholesale, as an update does, so files a release no longer ships don't linger.
Type: filesandordirs; Name: "{app}\runtime"
Type: filesandordirs; Name: "{app}\Plugins"
; A release the updater staged must never apply over this one.
Type: filesandordirs; Name: "{app}\update"

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; The marker that this folder is a release install: only then does the client look for updates.
Source: "install.json"; DestDir: "{app}"; Flags: ignoreversion

[UninstallDelete]
; Updates may have added files Setup never logged. Player files (window-positions.txt, the retail-*-native switch
; files, logs) sit at the root and are left alone.
Type: filesandordirs; Name: "{app}\runtime"
Type: filesandordirs; Name: "{app}\Plugins"
Type: filesandordirs; Name: "{app}\update"
; The update check's time: left behind, it would delay the first check after a reinstall.
Type: files; Name: "{app}\update-check"

[Code]
const
  DecalKey = 'SOFTWARE\Decal';
  FiltersKey = 'SOFTWARE\Decal\NetworkFilters';
  { The stub's assembly GUID, pinned in Plugin.cs. HKLM32 is WOW6432Node on 64-bit Windows. }
  FilterKey = 'SOFTWARE\Decal\NetworkFilters\{00156974-B6EE-4990-A850-4E3B55871654}';
  StubObject = 'LegACEy.Client.DecalPlugin.Plugin';

{ Fails open: a broken WMI never blocks setup or uninstall. }
function GameRunning: Boolean;
var
  Locator, Service, Processes: Variant;
begin
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''acclient.exe''');
    Result := Processes.Count > 0;
  except
    Result := False;
  end;
end;

{ Runs reg.exe commands in one elevated cmd, so the player sees one UAC prompt. Callers check the registry afterwards
  rather than trusting the exit code. }
procedure RunElevated(const Commands: String);
var
  ErrorCode: Integer;
begin
  ShellExec('runas', ExpandConstant('{cmd}'), '/s /c "' + Commands + '"', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
end;

function RegAdd(const Value: String): String;
begin
  Result := 'reg add "HKLM\' + FilterKey + '" ' + Value + ' /f /reg:32';
end;

{ reg.exe deletes for any other key registering the stub, such as a manual registration from before its GUID was
  pinned, which would load a second copy. }
function OtherRegistrations: String;
var
  Names: TArrayOfString;
  I: Integer;
  Key, Value: String;
begin
  Result := '';
  if RegGetSubkeyNames(HKLM32, FiltersKey, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do begin
      Key := FiltersKey + '\' + Names[I];
      if (CompareText(Key, FilterKey) <> 0) and RegQueryStringValue(HKLM32, Key, 'Object', Value) then
        if Value = StubObject then
          Result := Result + ' && reg delete "HKLM\' + Key + '" /f /reg:32';
    end;
end;

function InitializeSetup: Boolean;
begin
  Result := RegKeyExists(HKLM32, DecalKey);
  if not Result then
    SuppressibleMsgBox('Decal is not installed. Install Decal first, then run this setup again.', mbCriticalError, MB_OK, IDOK);
end;

{ Registers the plugin before any file is copied, so a refused UAC prompt leaves nothing half installed. An existing
  registration (a manual install) is moved onto this folder and enabled, and one under another key is removed. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  App, Path: String;
  Enabled: Cardinal;
begin
  if GameRunning then begin
    Result := 'Asheron''s Call is running. Close every game client, then run setup again.';
    exit;
  end;
  App := ExpandConstant('{app}');
  RunElevated(RegAdd('/ve /d "LegACEy Decal Plugin"') + ' && ' +
    RegAdd('/v Assembly /d "LegACEy.Client.DecalPlugin.dll"') + ' && ' +
    RegAdd('/v Path /d "' + App + '"') + ' && ' +
    RegAdd('/v Object /d "' + StubObject + '"') + ' && ' +
    RegAdd('/v Surrogate /d "{71A69713-6593-47EC-0002-0000000DECA1}"') + ' && ' +
    RegAdd('/v Enabled /t REG_DWORD /d 1') + OtherRegistrations);
  if not (RegQueryStringValue(HKLM32, FilterKey, 'Path', Path) and (CompareText(Path, App) = 0) and
          RegQueryDWordValue(HKLM32, FilterKey, 'Enabled', Enabled) and (Enabled = 1)) then
    Result := 'Setup could not register the plugin in Decal. Registering needs administrator approval; run setup again and allow it.';
end;

{ Removes the Decal entry before any file goes; without administrator approval nothing is uninstalled. }
function InitializeUninstall: Boolean;
begin
  Result := not GameRunning;
  if not Result then begin
    SuppressibleMsgBox('Asheron''s Call is running. Close every game client, then uninstall again.', mbError, MB_OK, IDOK);
    exit;
  end;
  if RegKeyExists(HKLM32, FilterKey) then
    RunElevated('reg delete "HKLM\' + FilterKey + '" /f /reg:32');
  Result := not RegKeyExists(HKLM32, FilterKey);
  if not Result then
    SuppressibleMsgBox('Uninstalling needs administrator approval to remove the plugin from Decal. Uninstall again and allow it.', mbError, MB_OK, IDOK);
end;
