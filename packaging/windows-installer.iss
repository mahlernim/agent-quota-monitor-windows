#ifndef AppVersion
#define AppVersion "0.2.0-beta.6"
#endif
#ifndef BundleDir
#error BundleDir required
#endif
#ifndef OutputDir
#error OutputDir required
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
[Icons]
Name: "{group}\Agent Quota Monitor"; Filename: "{app}\agent-quota-monitor-windows.exe"
Name: "{autodesktop}\Agent Quota Monitor"; Filename: "{app}\agent-quota-monitor-windows.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\agent-quota-monitor-windows.exe"; Description: "Launch Agent Quota Monitor"; Flags: nowait postinstall skipifsilent
[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Command: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AgentQuotaMonitorWindows', Command) then
      if CompareText(Command, '"' + ExpandConstant('{app}\agent-quota-monitor-windows.exe') + '" --minimized') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AgentQuotaMonitorWindows');
end;
