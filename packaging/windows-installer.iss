#ifndef AppVersion
#error AppVersion required
#endif
#ifndef BundleDir
#error BundleDir required
#endif
#ifndef OutputDir
#error OutputDir required
#endif
#ifndef ManifestFile
#error ManifestFile required
#endif
[Setup]
AppId={{AC51B847-3CB2-4E11-9B98-B6F5090F384D}
AppName=Agent Quota Monitor
AppVersion={#AppVersion}
AppPublisher=Agent Quota Monitor contributors
AppPublisherURL=https://github.com/mahlernim/agent-quota-monitor-windows
DefaultDirName={localappdata}\Programs\Agent Quota Monitor
DefaultGroupName=Agent Quota Monitor
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=agent-quota-monitor-windows-{#AppVersion}-setup-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\agent-quota-monitor-windows.exe
AppMutex=Local\AgentQuotaMonitorWpf
CloseApplications=no
RestartApplications=no
LicenseFile={#BundleDir}\LICENSE
[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked
[Files]
Source: "{#BundleDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ManifestFile}"; DestDir: "{app}"; DestName: "installed-files.txt"; Flags: ignoreversion
Source: "legacy-files.txt"; Flags: dontcopy
[Icons]
Name: "{group}\Agent Quota Monitor"; Filename: "{app}\agent-quota-monitor-windows.exe"
Name: "{autodesktop}\Agent Quota Monitor"; Filename: "{app}\agent-quota-monitor-windows.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\agent-quota-monitor-windows.exe"; Description: "Launch Agent Quota Monitor"; Flags: nowait postinstall skipifsilent
[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var ResultCode: Integer;
begin
  { AppMutex guarantees that no monitor window is open, so any running reader was left behind,
    for example after a crash. Stop it so its files can be replaced. Cached data is written atomically. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM quota-backend.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

{ Rejects manifest entries that could reach outside the app folder. }
function SafeEntry(const Entry: String): Boolean;
begin
  Result := (Entry <> '') and (Entry[1] <> '\') and (Pos('..', Entry) = 0) and (Pos(':', Entry) = 0);
end;

procedure DeleteStale(const Entry: String; Keep, Folders: TStringList);
begin
  if Keep.IndexOf(Lowercase(Entry)) >= 0 then Exit;
  if DeleteFile(ExpandConstant('{app}\') + Entry) and (ExtractFileDir(Entry) <> '') then
    Folders.Add(ExtractFileDir(Entry));
end;

{ Deletes files that the previous version installed and this version no longer ships.
  Only files named in the previous manifest are touched, so other files in a shared folder stay. }
procedure RemoveStaleFiles;
var Previous, Current: TArrayOfString;
    Keep, Folders: TStringList;
    Find: TFindRec;
    Source, Entry, Folder: String;
    I: Integer;
begin
  Source := ExpandConstant('{app}\installed-files.txt');
  if not FileExists(Source) then
  begin
    { Earlier installers wrote no manifest. Use the fixed list, but only over an existing installation. }
    if not FileExists(ExpandConstant('{app}\unins000.exe')) or not FileExists(ExpandConstant('{app}\agent-quota-monitor-windows.exe')) then Exit;
    ExtractTemporaryFile('legacy-files.txt');
    Source := ExpandConstant('{tmp}\legacy-files.txt');
  end;
  ExtractTemporaryFile('installed-files.txt');
  if not LoadStringsFromFile(Source, Previous) or not LoadStringsFromFile(ExpandConstant('{tmp}\installed-files.txt'), Current) then Exit;
  Keep := TStringList.Create;
  Folders := TStringList.Create;
  try
    Keep.Sorted := True;
    Folders.Sorted := True;
    Folders.Duplicates := dupIgnore;
    for I := 0 to GetArrayLength(Current) - 1 do
      Keep.Add(Lowercase(Trim(Current[I])));
    for I := 0 to GetArrayLength(Previous) - 1 do
    begin
      Entry := Trim(Previous[I]);
      StringChangeEx(Entry, '/', '\', True);
      if (Entry = '') or (Entry[1] = '#') or not SafeEntry(Entry) then Continue;
      if Pos('*', Entry) = 0 then
        DeleteStale(Entry, Keep, Folders)
      else if FindFirst(ExpandConstant('{app}\') + Entry, Find) then
      try
        repeat
          if Find.Attributes and FILE_ATTRIBUTE_DIRECTORY = 0 then
            DeleteStale(AddBackslash(ExtractFileDir(Entry)) + Find.Name, Keep, Folders);
        until not FindNext(Find);
      finally
        FindClose(Find);
      end;
    end;
    { Remove folders left empty, deepest first. RemoveDir fails on folders that still hold files. }
    for I := Folders.Count - 1 downto 0 do
    begin
      Folder := Folders[I];
      while (Folder <> '') and RemoveDir(ExpandConstant('{app}\') + Folder) do
        Folder := ExtractFileDir(Folder);
    end;
  finally
    Keep.Free;
    Folders.Free;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    RemoveStaleFiles;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Command: String;
    ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM quota-backend.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AgentQuotaMonitorWindows', Command) then
      if CompareText(Command, '"' + ExpandConstant('{app}\agent-quota-monitor-windows.exe') + '" --minimized') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AgentQuotaMonitorWindows');
end;
